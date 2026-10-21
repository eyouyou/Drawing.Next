using Hevo.Charting.Core;

namespace Hevo.Charting
{
    /// <summary>
    /// 按类型化请求(demand)拉数据、由框架维护代号的数据源。
    /// <para>
    /// 为什么要它:<see cref="ReactiveDataSource{TSource, TContext, TItem}"/> 只管切换,业务的翻页 / 刷新请求走自带的 RequestBus,
    /// 两条通道互不取消,切换后旧上下文的迟到回包会并进新上下文。这里把两条合成一套规则:
    /// </para>
    /// <list type="bullet">
    ///   <item>切换不是一种请求:<see cref="ReactiveDataSource{TSource, TContext, TItem}.SwitchAsync"/> 让代号加一,上一代在途请求的令牌被取消、回包被丢弃。</item>
    ///   <item>新一代的首包(<see cref="CreateInitialDemand"/> 的回包)走 <see cref="ApplyReset"/> 整段替换,以 <see cref="DataChange.Reset"/> 发布;
    ///         旧数据是先清空还是留到这时由 <see cref="ResetPolicy"/> 决定。</item>
    ///   <item>首包到之前发的请求直接丢掉(旧数据还在时合并进去就是串台);首包之后同代的回包走 <see cref="ApplyMerge"/>,由业务按 demand 合并。</item>
    ///   <item>demand 是开放集合:加一种请求就加一个类型,在 <see cref="ApplyMerge"/> 里按类型分派,不改框架、不加枚举。</item>
    /// </list>
    /// <para>
    /// 存储不限:Apply 在锁内执行,子类改 <c>_buffer</c> 或自己的列存储都行,返回这次的 <see cref="DataChange"/>,框架负责发布。
    /// 数据不放 <c>_buffer</c> 的子类同时覆盖 <see cref="ReactiveDataSource{TSource, TContext, TItem}.ClearData"/>。
    /// </para>
    /// </summary>
    public abstract class DemandDataSource<TSource, TContext, TDemand, TResponse, TItem> : ReactiveDataSource<TSource, TContext, TItem>
        where TSource : DemandDataSource<TSource, TContext, TDemand, TResponse, TItem>
    {
        // 锁内读写:当前这一代的首包还没落地
        private bool _awaitingFirst;

        /// <summary>当前这一代首包还没到(切换中)。在 <c>_lock</c> 内读才和数据一致。</summary>
        protected bool IsAwaitingFirst
        {
            get { lock (_lock) return _awaitingFirst; }
        }

        /// <summary>切换后首包要什么(例如"最新 300 根")。</summary>
        protected abstract TDemand CreateInitialDemand(TContext context);

        /// <summary><see cref="RefreshAsync"/> 要什么(例如"最新 2 根")。默认同首包,回包按合并处理。</summary>
        protected virtual TDemand CreateRefreshDemand(TContext context) => CreateInitialDemand(context);

        /// <summary>按 demand 拉数据。令牌在切换时被取消。</summary>
        protected abstract Task<TResponse> OnFetchAsync(TContext context, TDemand demand, CancellationToken token);

        /// <summary>新一代首包:整段替换(锁内)。返回值一般是 <see cref="DataChange.Reset"/>。</summary>
        protected abstract DataChange ApplyReset(TContext context, TDemand demand, TResponse response);

        /// <summary>同代后续回包:按 demand 合并(锁内)。返回这次改了什么;<see cref="DataChange.IsEmpty"/> 时不发布。</summary>
        protected abstract DataChange ApplyMerge(TContext context, TDemand demand, TResponse response);

        /// <summary>回包条数,透传给切换 / 刷新的调用方。</summary>
        protected virtual int CountOf(TResponse response) => 0;

        protected override void OnGenerationStarted(TContext context, ResetPolicy policy)
        {
            _awaitingFirst = true;
            base.OnGenerationStarted(context, policy);
        }

        // 切换(含旧入口 SwitchContext / SwitchContextAsync / Load / 蓝图级联)都经内部单飞 bus 进到这里:拉首包、整段替换
        protected sealed override async Task<int> OnFetchAsync(TContext context, CancellationToken token)
        {
            TContext ctx;
            long generation;
            CancellationToken generationToken;
            lock (_lock)
            {
                ctx = Context!;
                generation = Generation;
                generationToken = GenerationToken;
            }

            var demand = CreateInitialDemand(ctx);
            TResponse response;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, generationToken);
                response = await OnFetchAsync(ctx, demand, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 首包失败:新上下文没有数据。留着的旧数据也撤掉(否则之后的同代回包会并进旧上下文),
                // 再放开同代请求,刷新 / 推送可以接着补数据,不卡在"切换中"
                lock (_lock)
                {
                    if (IsCurrentGeneration(generation) && _awaitingFirst)
                    {
                        _awaitingFirst = false;
                        ClearData();
                        Publish(DataChange.Reset);
                    }
                }
                throw;
            }

            lock (_lock)
            {
                if (!IsCurrentGeneration(generation)) return 0;
                var change = ApplyReset(ctx, demand, response);
                _awaitingFirst = false;
                Publish(change);
            }
            OnSwitched(ctx);
            return CountOf(response);
        }

        /// <summary>新一代首包发布之后(锁外)调,例如重新订阅实时推送。</summary>
        protected virtual void OnSwitched(TContext context) { }

        /// <summary>
        /// 同一上下文刷新(<see cref="CreateRefreshDemand"/>,回包合并),返回回包条数。
        /// 首包还没到(切换中)时什么都不做、返回 0:切换的首包本来就是最新数据;这时重拉会顶掉切换自己的请求。
        /// </summary>
        public override Task<int> RefreshAsync(CancellationToken token = default)
        {
            if (Context == null || IsAwaitingFirst) return Task.FromResult(0);
            return RefreshCoreAsync(token);
        }

        private async Task<int> RefreshCoreAsync(CancellationToken token)
        {
            TDemand demand;
            lock (_lock)
            {
                if (Context == null) return 0;
                demand = CreateRefreshDemand(Context);
            }
            var result = await RequestAsync(demand, token).ConfigureAwait(false);
            return result.Applied ? CountOf(result.Response) : 0;
        }

        /// <summary>
        /// 发一个同代请求,回包按 <see cref="ApplyMerge"/> 合并。并发请求互不取消(要单飞的业务自己合并在途任务)。
        /// 首包未到、请求途中发生切换、或回包时已不是这一代:不合并,返回 <see cref="DemandResult{TResponse}.Applied"/> = false。
        /// </summary>
        public async Task<DemandResult<TResponse>> RequestAsync(TDemand demand, CancellationToken token = default)
        {
            TContext ctx;
            long generation;
            CancellationToken generationToken;
            lock (_lock)
            {
                if (Context == null || _awaitingFirst) return default;
                ctx = Context;
                generation = Generation;
                generationToken = GenerationToken;
            }

            TResponse response;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, generationToken);
                response = await OnFetchAsync(ctx, demand, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !IsCurrentGenerationLocked(generation))
            {
                return default;
            }

            lock (_lock)
            {
                if (!IsCurrentGeneration(generation)) return default;
                var change = ApplyMerge(ctx, demand, response);
                if (!change.IsEmpty) Publish(change);
                return new DemandResult<TResponse>(true, response, change);
            }
        }

        private bool IsCurrentGenerationLocked(long generation)
        {
            lock (_lock) return IsCurrentGeneration(generation);
        }
    }

    /// <summary>一次同代请求的结局。<see cref="Applied"/> = false:被切换作废或首包未到,回包没有合并。</summary>
    public readonly struct DemandResult<TResponse>
    {
        public DemandResult(bool applied, TResponse response, DataChange change)
        {
            Applied = applied;
            Response = response;
            Change = change;
        }

        public bool Applied { get; }
        public TResponse Response { get; }

        /// <summary>合并后发布的变化(没变时为 <see cref="DataChange.None"/>)。</summary>
        public DataChange Change { get; }
    }
}
