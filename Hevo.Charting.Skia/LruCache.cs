using System;
using System.Collections.Generic;

namespace Hevo.Charting.Skia
{
    /// <summary>
    /// 有上限的 LRU(写法同 WpfDrawingRenderer 内部那份):Dictionary + 双向链表,命中 / 淘汰都是 O(1)。
    /// 值是原生资源(SKPaint / SKFont / SKPath),被挤出或清空时交给 <c>onEvict</c> 释放。
    /// </summary>
    internal sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Action<TValue>? _onEvict;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _list = new();

        public LruCache(int capacity, Action<TValue>? onEvict = null)
        {
            _capacity = capacity;
            _onEvict = onEvict;
            _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
        }

        public int Count => _map.Count;

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (!ReferenceEquals(node, _list.First))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                }
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }

        /// <summary>插入新 key(调用方保证 key 不在表里)。满了先淘汰最久未用的一项。</summary>
        public void Add(TKey key, TValue value)
        {
            if (_map.Count >= _capacity)
            {
                var oldest = _list.Last!;
                _map.Remove(oldest.Value.Key);
                _list.RemoveLast();
                _onEvict?.Invoke(oldest.Value.Value);
                oldest.Value = new KeyValuePair<TKey, TValue>(key, value);
                _list.AddFirst(oldest);
                _map[key] = oldest;
                return;
            }
            _map[key] = _list.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
        }

        public void Clear()
        {
            if (_onEvict != null)
                foreach (var kv in _list) _onEvict(kv.Value);
            _map.Clear();
            _list.Clear();
        }
    }
}
