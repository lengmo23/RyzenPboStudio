using System;

namespace ZenStates.Core.SMUCommands
{
    // Set Curve Shaper margin for one frequency tier (0..4), three temperature columns.
    // arg[0]: [31:24] high temp, [23:16] medium temp, [15:8] low temp, [7] write flag (0x80), [6:0] frequency tier.
    // Each margin is clamped to [-50, 30] and stored as a signed byte.
    internal class SetCurveShaperMargin : BaseSMUCommand
    {
        public SetCurveShaperMargin(SMU smu) : base(smu) { }

        public override bool CanExecute()
        {
            return smu.Rsmu.SMU_MSG_SetCurveShaperMargin > 0;
        }

        public CmdResult Execute(int marginHigh, int marginMedium, int marginLow, int frequencyTier)
        {
            if (frequencyTier < 0 || frequencyTier > 4)
                throw new ArgumentOutOfRangeException("frequencyTier", "Frequency tier must be between 0 and 4.");

            if (CanExecute())
            {
                result.args[0] = ((uint)EncodeMargin(marginHigh) << 24)
                               | ((uint)EncodeMargin(marginMedium) << 16)
                               | ((uint)EncodeMargin(marginLow) << 8)
                               | 0x80u
                               | ((uint)frequencyTier & 0x7Fu);
                result.status = smu.SendRsmuCommand(smu.Rsmu.SMU_MSG_SetCurveShaperMargin, ref result.args);
            }

            return base.Execute();
        }

        private static int EncodeMargin(int margin)
        {
            if (margin < -50) margin = -50;
            else if (margin > 30) margin = 30;
            // 项目全局启用 CheckForOverflowUnderflow，负数转 byte 需显式 unchecked，
            // 否则该转换在 checked 上下文中会抛 OverflowException。
            return unchecked((byte)(sbyte)margin);
        }
    }
}
