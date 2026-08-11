namespace ZenStates.Core.SMUCommands
{
    // Read all Curve Shaper margins via the read-only GetCurveShaperMargin message.
    // A single call returns one packed uint per frequency tier in args[0..4].
    //
    // WARNING: do NOT read via SetCurveShaperMargin — clearing its write bit still WRITES (zeroes the
    // tier, drops the applied voltage). Only the Get message is non-destructive.
    internal class GetAllCurveShaperMargins : BaseSMUCommand
    {
        public GetAllCurveShaperMargins(SMU smu) : base(smu) { }

        public override bool CanExecute()
        {
            return smu.Rsmu.SMU_MSG_GetCurveShaperMargin > 0;
        }

        public override CmdResult Execute()
        {
            if (CanExecute())
            {
                ResetArgs();
                result.status = smu.SendRsmuCommand(smu.Rsmu.SMU_MSG_GetCurveShaperMargin, ref result.args);
            }

            return base.Execute();
        }
    }
}
