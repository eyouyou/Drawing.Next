using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 散点摄入的空位填 MissingValue(double 为 NaN),不再用 0 冒充价格(负价 / 零价合约里 0 是合法值)。
    /// </summary>
    public sealed class MissingValueTests
    {
        public readonly record struct Bar(int Index, double Close);

        [Fact]
        public void MissingValue_IsNaNForFloatingPoint_DefaultOtherwise()
        {
            Assert.True(double.IsNaN(MissingValue<double>.Value));
            Assert.True(float.IsNaN(MissingValue<float>.Value));
            Assert.Equal(0, MissingValue<int>.Value);
            Assert.Equal(default(DateTime), MissingValue<DateTime>.Value);
            Assert.True(double.IsNaN((double)MissingValue.For(typeof(double))!));
            Assert.Null(MissingValue.For(typeof(string)));
        }

        [Fact]
        public void ScatterGaps_AreNaN_NotZero()
        {
            // 3 个位置只映射到第 0 个:空位必须是 NaN(0 在负价 / 零价合约里是合法价格)
            var port = new DataPort<ReadOnlyMemory<double>>("gap_close");
            var ingestor = new ScatterIngestor<Bar, double>(port, () => 3, MissingValue<double>.Value, b => b.Index, b => b.Close);
            using var board = new DataBlackboard();
            var items = new[] { new Bar(0, 42) };
            using (board.WriteScope())
                ingestor.Process(new DataSnapshot<Bar>(items, 1, new VersionToken(1)), board);

            double[] col;
            using (board.ReadScope()) col = board.Read(port).ToArray();
            Assert.Equal(3, col.Length);
            Assert.Equal(42, col[0]);
            Assert.True(double.IsNaN(col[1]));
            Assert.True(double.IsNaN(col[2]));
        }
    }
}
