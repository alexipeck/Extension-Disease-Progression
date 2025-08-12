using Landis.Core;
using Landis.SpatialModeling;
using System.Collections.Generic;
using System.Linq;
using System.Dynamic;
using Landis.Library.UniversalCohorts;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public class PlugIn
        : ExtensionMain
    {
        public static readonly ExtensionType type = new ExtensionType("disturbance:DP");
        public static readonly string ExtensionName = "Disease Progression";
        private static ICore modelCore;
        private IInputParameters parameters;
        //---------------------------------------------------------------------

        public PlugIn()
            : base(ExtensionName, type)
        {}

        //---------------------------------------------------------------------

        public override void LoadParameters(string dataFile, ICore mCore)
        {
            modelCore = mCore;
     
            InputParametersParser parser = new InputParametersParser();
            parameters = Data.Load<IInputParameters>(dataFile, parser);
        }

        //---------------------------------------------------------------------

        public static ICore ModelCore
        {
            get
            {
                return modelCore;
            }
        }
       
        public override void Initialize()
        {
            ModelCore.UI.WriteLine("Species selected for disease progression:");
            SiteVars.Initialize(ModelCore);
            foreach (string speciesName in parameters.SpeciesTransitionMatrix.Keys) {
                ModelCore.UI.WriteLine($"{speciesName}");
            }
            ModelCore.UI.WriteLine("");
            Timestep = parameters.Timestep;
            ModelCore.UI.WriteLine("Disease progression initialized");
        }

        //---------------------------------------------------------------------
        public override void Run()
        {
            ModelCore.UI.WriteLine("Running disease progression");
            ////////
            //DEBUG PARAMETERS
            bool debugDisableDiseaseProgressionKill = false;
            bool debugOnlyOneTransferPerSitePerTimestep = false;
            bool debugOutputTransitions = true;
            bool debugDumpSiteInformation = false;
            ////////
            
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;
            
            // Species string to ISpecies lookup
            Dictionary<string, ISpecies> speciesNameToISpecies = new Dictionary<string, ISpecies>();
            foreach (var species in ModelCore.Species) {
                speciesNameToISpecies[species.Name] = species;
            }
            
            Dictionary<ISpecies, Dictionary<ushort, int>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, int>>();
            foreach (ActiveSite site in sites) {
                SiteCohorts siteCohorts = SiteVars.Cohorts[site];

                
                if (debugDumpSiteInformation) {
                    // Output existing state during timestep before any changes occur
                    foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                        foreach (ICohort cohort in speciesCohorts) {
                            ModelCore.UI.WriteLine($"Before disease progression: Site: ({site.Location.Row},{site.Location.Column}), Species: {speciesCohorts.Species.Name}, Age: {cohort.Data.Age}, Biomass: {cohort.Data.Biomass}");
                        }
                    }
                }
                
                bool hasTransitioned = false;
                foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    foreach (var (cohort, index) in concreteSpeciesCohorts.Select((cohort, index) => (cohort, index))) {
                        Cohort concreteCohort = (Cohort)cohort;

                        //process entry through matrix
                        string transitionToSpecies = parameters.GetTransitionMatrixOutcome(speciesCohorts.Species.Name, !(debugOnlyOneTransferPerSitePerTimestep && hasTransitioned));

                        //no transition will occur
                        if (transitionToSpecies == null) {
                            if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, int>();
                            }
                            if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = 0;
                            }
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] += concreteCohort.Data.Biomass;
                            continue; //short-circuit
                        }

                        //transitions to dead
                        if (transitionToSpecies.ToUpper() == "DEAD") {
                            if (debugDisableDiseaseProgressionKill) {
                                if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                    newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, int>();
                                }
                                if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                    newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = 0;
                                }
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] += concreteCohort.Data.Biomass;
                                if (debugOutputTransitions) {
                                    ModelCore.UI.WriteLine($"(inert) Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                                }
                                hasTransitioned = true;
                                continue; //short-circuit
                            }

                            Cohort.CohortMortality(concreteSpeciesCohorts, concreteCohort, site, null, 1f);
                            if (debugOutputTransitions) {
                                ModelCore.UI.WriteLine($"Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                            }
                            
                            hasTransitioned = true;
                            continue; //short-circuit
                        }

                        double biomassTransferModifier = 0.3; //TODO: Switch to dynamic value for biomass transfer
                        if (debugOnlyOneTransferPerSitePerTimestep && hasTransitioned) {
                            biomassTransferModifier = 0.0;
                        }
                        if (debugOnlyOneTransferPerSitePerTimestep) {
                            hasTransitioned = true;
                        }

                        //transitions to another species
                        int transfer = (int)(concreteCohort.Data.Biomass * biomassTransferModifier);
                        ISpecies targetSpecies = speciesNameToISpecies[transitionToSpecies];

                        //push biomass to target species cohort
                        if (!newSiteCohortsDictionary.ContainsKey(targetSpecies)) {
                            newSiteCohortsDictionary[targetSpecies] = new Dictionary<ushort, int>();
                        }
                        if (!newSiteCohortsDictionary[targetSpecies].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] = 0;
                        }
                        newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] += transfer;
                        

                        //push biomass to original species cohort
                        if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                            newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, int>();
                        }
                        if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = 0;
                        }
                        newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] += concreteCohort.Data.Biomass - transfer;
                        if (debugOutputTransitions && transfer > 0) {
                            ModelCore.UI.WriteLine($"Transferred {transfer} biomass from {speciesCohorts.Species.Name} to {targetSpecies.Name}");
                        }
                    }
                }

                //rewrite SiteCohorts() regardless of changes
                //TODO: Create a clone of SiteCohorts minus the cohortData
                //seemingly not necessary though
                var newSiteCohorts = new SiteCohorts();
                foreach (var species in newSiteCohortsDictionary) {
                    foreach (var cohort in species.Value) {
                        if (cohort.Value > 0) {
                            newSiteCohorts.AddNewCohort(species.Key, cohort.Key, cohort.Value, new ExpandoObject());
                        }
                    }
                }
                foreach (ISpeciesCohorts speciesCohorts in newSiteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    concreteSpeciesCohorts.UpdateMaturePresent();
                }
                if (debugDumpSiteInformation) {
                    // Output existing state during timestep after any changes occur
                    foreach (ISpeciesCohorts speciesCohorts in newSiteCohorts) {
                        foreach (ICohort cohort in speciesCohorts) {
                            ModelCore.UI.WriteLine($"After disease progression: Site: ({site.Location.Row},{site.Location.Column}), Species: {speciesCohorts.Species.Name}, Age: {cohort.Data.Age}, Biomass: {cohort.Data.Biomass}");
                        }
                    }
                }
                SiteVars.Cohorts[site] = newSiteCohorts;
                foreach (var data in newSiteCohortsDictionary) {
                    data.Value.Clear();
                }
            }
        }
        public override void AddCohortData() { return; }
    }
}
