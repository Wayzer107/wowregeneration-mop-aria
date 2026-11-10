namespace WoWRegeneration.Repositories
{
    /// <summary>
    ///     Mists of Pandaria 5.4.8 (build 18414). The manifest describes the 5.0.3 (15890) base
    ///     install plus every update to 18414. Only the NA path still serves it.
    /// </summary>
    public class WoW548 : WoWRepository
    {
        public override string GetBaseUrl()
        {
            return "http://dist.blizzard.com.edgesuite.net/wow-pod-retail/NA/15890.direct/";
        }

        public override string GetMFilName()
        {
            return "wow-18414-447E3E618F731CCBF4F7D2C4E56C5644.mfil";
        }
    }
}
