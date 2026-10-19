using System;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 列端口的引用版本:底层引用(数组 / 起点 / 长度)换了才变,原地改再 ForceWrite 同一块 ROM 不变;非列端口不记。
    /// </summary>
    public sealed class BlackboardIdentityVersionTests
    {
        private static readonly DataPort<ReadOnlyMemory<double>> Column = new("T_Column");
        private static readonly DataPort<double> Scalar = new("T_Scalar");

        private static void Write<T>(DataBlackboard board, DataPort<T> port, T value)
        {
            using (board.AcquireWriteLock()) board.ForceWrite(port, value);
        }

        [Fact]
        public void SameMemory_KeepsIdentity_ButBumpsVersion()
        {
            using var board = new DataBlackboard();
            var a = new double[8];
            Write(board, Column, new ReadOnlyMemory<double>(a, 0, 5));
            var id = board.IdentityOf(Column);
            var ver = board.VersionOf(Column);

            a[4] = 1; // 原地改最后一根
            Write(board, Column, new ReadOnlyMemory<double>(a, 0, 5));

            Assert.Equal(id, board.IdentityOf(Column));
            Assert.NotEqual(ver, board.VersionOf(Column));
        }

        [Fact]
        public void LengthOrArrayChange_BumpsIdentity_EvenWhenSwappedBack()
        {
            using var board = new DataBlackboard();
            var a = new double[8];
            var b = new double[8];
            Write(board, Column, new ReadOnlyMemory<double>(a, 0, 5));
            var id0 = board.IdentityOf(Column);

            Write(board, Column, new ReadOnlyMemory<double>(a, 0, 6)); // 追加
            var id1 = board.IdentityOf(Column);
            Assert.NotEqual(id0, id1);

            Write(board, Column, new ReadOnlyMemory<double>(b, 0, 6)); // 换块
            Write(board, Column, new ReadOnlyMemory<double>(a, 0, 6)); // 换回同一块
            Assert.NotEqual(id1, board.IdentityOf(Column));
        }

        [Fact]
        public void NonColumnPort_HasNoIdentity()
        {
            using var board = new DataBlackboard();
            Write(board, Scalar, 1.0);
            Write(board, Scalar, 2.0);
            Assert.Equal(default, board.IdentityOf(Scalar));
        }
    }
}
