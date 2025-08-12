using Landis.Core;
using Landis.SpatialModeling;
using Landis.Library.UniversalCohorts;

namespace Landis.Extension.Disturbance.DiseaseProgression
{    public static class SiteVars
    {
        private static ISiteVar<SiteCohorts> universalCohorts;

        public static void Initialize(ICore modelCore) {
            universalCohorts = PlugIn.ModelCore.GetSiteVar<SiteCohorts>("Succession.UniversalCohorts");
        }

        public static ISiteVar<SiteCohorts> Cohorts
        {
            get
            {
                return universalCohorts;
            }
            set
            {
                universalCohorts = value;
            }
        }
    }
}
