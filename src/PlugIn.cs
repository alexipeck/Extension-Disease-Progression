using Landis.Core;
using Landis.SpatialModeling;
using System.Collections.Generic;
using System.Linq;
using System.Dynamic;
using Landis.Library.UniversalCohorts;
using System.Diagnostics;
using System;
using System.Threading.Tasks;
using Landis.Library.Succession;
using static Landis.Extension.Disturbance.DiseaseProgression.Auxiliary;
using static Landis.Extension.Disturbance.DiseaseProgression.SiteVars;
using System.Drawing;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public class PlugIn
        : ExtensionMain
    {
        public static readonly ExtensionType type = new ExtensionType("disturbance:DP");
        public static readonly string ExtensionName = "Disease Progression";
        private static ICore modelCore;
        private IInputParameters parameters;
        public static Random rand = new Random();
        
        //---------------------------------------------------------------------

        public PlugIn()
            : base(ExtensionName, type)
        {}

        //---------------------------------------------------------------------

        public override void LoadParameters(string dataFile, ICore mCore)
        {
            modelCore = mCore;
            string extension = System.IO.Path.GetExtension(dataFile)?.ToLowerInvariant();
            if (extension != ".toml")
            {
                throw new System.Exception("Configuration must be a TOML file with a .toml extension.");
            }
            parameters = TomlInputLoader.Load(dataFile, modelCore);
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
            SiteVars.Initialize(ModelCore, parameters);
            ModelCore.UI.WriteLine("Species selected for disease progression:");
            foreach (ISpecies speciesName in parameters.SpeciesTransitionAgeMatrix.Keys) {
                ModelCore.UI.WriteLine($"{speciesName.Name}");
            }
            ModelCore.UI.WriteLine("");
            Timestep = parameters.Timestep;
            
            string[] pathsToEmpty = new string[] { "./infection_timeline", "./shi_timeline", "./shim_timeline", "./shim_normalized_timeline", "./foi_timeline", "./foi_colourised_timeline", "./infection_timeline_multi", "./overall_timeline" };
            foreach (string path in pathsToEmpty) {
                if (System.IO.Directory.Exists(path)) {
                    System.IO.DirectoryInfo directory = new System.IO.DirectoryInfo(path);
                    foreach (System.IO.FileInfo file in directory.GetFiles()) {
                        file.Delete();
                    }
                    ModelCore.UI.WriteLine($"Emptied folder: {path}");
                }
            }
            
            ModelCore.UI.WriteLine("Disease progression initialized");
        }

        //---------------------------------------------------------------------
        public override void Run()
        {
            //DumpSiteInformation(ModelCore.Landscape.ActiveSites);
            ModelCore.UI.WriteLine("Running disease progression");
            //////// DEBUG PARAMETERS
            bool debugOutputTransitions = false;
            bool debugDumpSiteInformation = false;
            //TODO: Image generation gets started in another thread
            //      I need a boolean check to ensure that the thread has stopped
            //      The thread is needed as is can save something like 7 steps per timestep
            ////////
            
            int distanceDispersalDecayMatrixWidth = DistanceDispersalDecayMatrixWidth;
            int distanceDispersalDecayMatrixHeight = DistanceDispersalDecayMatrixHeight;
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;
            (int x, int y) worstCaseMaximumUniformDispersalDistance = GetWorstCaseMaximumUniformDispersalDistance();

            int landscapeX = LandscapeDimensions.x;
            int landscapeY = LandscapeDimensions.y;
            int landscapeSize = landscapeX * landscapeY;
            int[] activeSiteIndices = ActiveSiteIndices;
            (int x, int y)[] precomputedLandscapeCoordinates = PrecomputedLandscapeCoordinates;
            (int x, int y)[] precomputedDispersalDistanceOffsets = PrecomputedDispersalDistanceOffsets;
            HashSet<int> activeSiteIndicesSet = ActiveSiteIndicesSet;

            Stopwatch globalTimer = new Stopwatch();
            globalTimer.Start();

            Stopwatch stopwatch = new Stopwatch();

            //////// Young infected to healthy replacement
            /// NOTE: This is entirely to counteract the succession libraries simulated natural spread
            stopwatch.Start();
            ReplaceAge1InfectedWithHealthy(sites, parameters);
            ModelCore.UI.WriteLine($"Finished replacing age 1 infected with healthy: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ////////

            ////////Resprouting TODO: REWORK
            stopwatch.Start();
			foreach (KeyValuePair<(int siteIndex, ISpecies species), ushort> entry in ResproutRemaining) {
				if (entry.Value == 0) continue;
				double random = rand.NextDouble();
				if (random <= 0.15) {
					(int x, int y) coordinates = PrecomputedLandscapeCoordinates[entry.Key.siteIndex];
					ActiveSite site = ModelCore.Landscape[new Location(coordinates.y + 1, coordinates.x + 1)];
					Reproduction.AddNewCohort(entry.Key.species, site, "resprout", 0);
				}
			}
			DecrementResproutLifetimes();
            ModelCore.UI.WriteLine($"Finished resprouting: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ////////

            stopwatch.Start();
            (bool[] sitesForProportioning,
             List<int> healthySitesListIndices,
             List<int> infectedSitesListIndices,
             List<int> ignoredSitesListIndices) = 
                InfectionStateDetection(sites, parameters, landscapeX, landscapeSize);
            ModelCore.UI.WriteLine($"Finished infection state detection: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();

            stopwatch.Start();
            if (Timestep == 1) {
                //set default probabilities from infection state
                SetDefaultProbabilities(healthySitesListIndices, infectedSitesListIndices, ignoredSitesListIndices);
            } else {
                EnforceInfectedProbability(infectedSitesListIndices);
                //modify probabilities based on their difference from previous timestep
                //if a site which was infected last timestep no longer is, set it back to 1 for susceptible, 0 for the others
                //I'm unsure if I need to track when a healthy becomes infected because it's handled only within this process
                //do we want to say that when a site is infected, that it's probability should be reset back to 1
                //or do we leave it to dynamically change based existing math?
            }
            ModelCore.UI.WriteLine($"Finished enforcing infected probability: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            
            double[] SHIM = new double[landscapeSize];

            ////////calculate SHI & SHIM
            stopwatch.Start();
            double SHIMSum = 0.0;
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                int index = CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX);
                double SHI = (int)CalculateSiteHostIndex(site);
                //TODO: Add land type modifier (LTM) and summed disturbance modifiers
                IEcoregion ecoregion = ModelCore.Ecoregion[site];
                SHIM[index] = CalculateSiteHostIndexModified(SHI, 0.0, 0.0);
                SHIMSum += SHIM[index];
            }
            ModelCore.UI.WriteLine($"Finished calculating SHI & SHIM: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(SHIM, "./shim_timeline/shim_state", "SHIM");
            
            stopwatch.Start();
            //double SHIMMean = SHIMSum / ModelCore.Landscape.ActiveSiteCount;
            foreach (int i in activeSiteIndices) {
                (int x, int y) targetCoordinates = PrecomputedLandscapeCoordinates[i];
                double sum = 0.0;
                int count = 0;
                foreach ((int x, int y) in precomputedDispersalDistanceOffsets) {
                    if (targetCoordinates.x + x < 0
                        || targetCoordinates.x + x >= landscapeX
                        || targetCoordinates.y + y < 0
                        || targetCoordinates.y + y >= landscapeY
                    ) continue;
                    int index = CalculateCoordinatesToIndex(x, y, landscapeX);
                    int j = index + i;
                    if (!activeSiteIndicesSet.Contains(j)) continue;
                    sum += SHIM[j];
                    count++;
                }
                double SHIMMean = sum / count;
                //TODO: Consider adding a branch to skip if SHIM[i] isn't more
                //      than 0 but mathematically it's fine unless SHIMMean is 0
                SHIM[i] /= SHIMMean;
            }
            ModelCore.UI.WriteLine($"Finished normalizing SHIM: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(SHIM, "./shim_normalized_timeline/shim_normalized_state", "SHI Normalized");

            stopwatch.Start();
            double[] FOI = CalculateForceOfInfection(landscapeSize, SHIM);
            ModelCore.UI.WriteLine($"Finished calculating FOI: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(FOI, "./foi_timeline/foi_state", "FOI");
            double[] FOIScaled = new double[landscapeSize];
            (double FOImin, double FOImax) = MinMaxActive(FOI);
            double range = FOImax - FOImin;
            if (range == 0.0) {
                for (int i = 0; i < landscapeSize; i++) {
                    FOIScaled[i] = 0.0;
                }
            } else {
                foreach (int i in activeSiteIndices) {
                    FOIScaled[i] = (FOI[i] - FOImin) / range;
                }
            }
            //TODO: Why did I do this?
            /* for (int i = 0; i < landscapeSize; i++) {
                FOIScaled[i] = FOI[i];
            } */
            ExportIntensityBitmap(FOIScaled, "./foi_colourised_timeline/foi_colourised_state", "FOI Colourised");

            stopwatch.Start();
            foreach (int healthySiteIndex in healthySitesListIndices) {
                double random = rand.NextDouble();
                if (random <= FOI[healthySiteIndex]) {
                    sitesForProportioning[healthySiteIndex] = true;
                }
            }
            
            ModelCore.UI.WriteLine($"Finished determining which sites are newly infected: {stopwatch.ElapsedMilliseconds} ms");
            ///////////////////
            
            stopwatch.Start();
            Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>>();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                if (!sitesForProportioning[CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX)]) continue;
                SiteCohorts siteCohorts = SiteVars.Cohorts[site];

                if (debugDumpSiteInformation) {
                    // Output existing state during timestep before any changes occur
                    foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                        foreach (ICohort cohort in speciesCohorts) {
                            ModelCore.UI.WriteLine($"Before disease progression: Site: ({siteLocation.Row},{siteLocation.Column}), Species: {speciesCohorts.Species.Name}, Age: {cohort.Data.Age}, Biomass: {cohort.Data.Biomass}");
                        }
                    }
                }
                
                foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    foreach (ICohort cohort in concreteSpeciesCohorts) {
                        Cohort concreteCohort = (Cohort)cohort;
                        ISpecies designatedHealthySpecies = parameters.GetDesignatedHealthySpecies(speciesCohorts.Species);

                        //process entry through matrix
                        (ISpecies, double)[] transitionDistribution = parameters.GetSpeciesTransitionAgeMatrixDistribution(speciesCohorts.Species, cohort.Data.Age);

                        //no transition will occur
                        if (transitionDistribution == null) {
                            if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                            }
                            if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                            }
                            (int biomass, Dictionary<string, int> additionalParameters) entry = newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age];
                            entry.biomass += concreteCohort.Data.Biomass;
                            foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                                if (!entry.additionalParameters.ContainsKey(parameter.Key)) {
                                    entry.additionalParameters[parameter.Key] = 0;
                                }
                                entry.additionalParameters[parameter.Key] += (int)parameter.Value;
                            }
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = entry;
                            continue; //short-circuit
                        }

                        //exists to account for the error created during the cast from float to int
                        //with low biomass values, the error accumilation can be significant
                        //biomass being stored as an int is a design issue in landis-core which
                        //results in biomass not being kept in the live cohort or going to decomposition
                        //pools as a result of Math.Floor being used in truncation of positive float values
                        //to int it simply gets discarded
                        int totalBiomassAccountedFor = 0;
                        int remainingBiomass = concreteCohort.Data.Biomass;

                        Dictionary<string, int> remainingAdditionalParameters = new Dictionary<string, int>();
                        Console.WriteLine("ConcreteCohort.Data.AdditionalParameters:");
                        foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                            Console.WriteLine($"1Parameter: {parameter.Key}, Value: {parameter.Value}");
                            remainingAdditionalParameters[parameter.Key] = (int)parameter.Value;
                        }
                        foreach ((ISpecies targetSpecies, double proportion) in transitionDistribution) {
                            //null case is the no change case within the matrix accounting for either
                            //the user specified proportion in the case of all proportions for a line
                            //adding up to 1.0, in all other cases, the null case equals the user specified
                            //proportion + the remaining proportion
                            if (targetSpecies != null) {
                                if (remainingBiomass == 0) {
                                    break;
                                }
                                //ModelCore.UI.WriteLine($"Before rounding: {concreteCohort.Data.Biomass * proportion}");
                                int transfer = (int)Math.Round(concreteCohort.Data.Biomass * proportion);
                                //ModelCore.UI.WriteLine($"Rounded: {Math.Round(concreteCohort.Data.Biomass * proportion)}");
                                //ModelCore.UI.WriteLine($"Cast: {transfer}");
                                if (remainingBiomass - transfer < 0) {
                                    transfer = remainingBiomass;
                                }
                                remainingBiomass -= transfer;
                                totalBiomassAccountedFor += transfer;
                                if (targetSpecies == null || concreteCohort.Data.Biomass == 1) {
                                    //This is a hacky way to kill miniscule cohorts
                                    if (concreteCohort.Data.Biomass == 1) {
                                        transfer = 1;
                                        remainingBiomass -= transfer;
                                        totalBiomassAccountedFor += transfer;
                                    }
                                    //TODO: Should I be feeding 1.0 for the proportion here so it kills the entire cohort in the case of where biomass == 1?
                                    Cohort.CohortMortality(concreteSpeciesCohorts, concreteCohort, site, type, (float)proportion);
                                    if (debugOutputTransitions) {
                                        ModelCore.UI.WriteLine($"Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                                    }
                                    AddResproutLifetime(CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX), designatedHealthySpecies);
                                    continue; //short-circuit
                                }

                                //push biomass to target species cohort
                                if (!newSiteCohortsDictionary.ContainsKey(targetSpecies)) {
                                    newSiteCohortsDictionary[targetSpecies] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                                }
                                if (!newSiteCohortsDictionary[targetSpecies].ContainsKey(concreteCohort.Data.Age)) {
                                    newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                                }
                                (int biomass, Dictionary<string, int> additionalParameters) entry = newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age];
                                entry.biomass += transfer;
                                foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                                    if (!entry.additionalParameters.ContainsKey(parameter.Key)) {
                                        entry.additionalParameters[parameter.Key] = 0;
                                    }
                                    entry.additionalParameters[parameter.Key] += (int)parameter.Value;
                                    remainingAdditionalParameters[parameter.Key] -= (int)parameter.Value;
                                    Trace.Assert((int)remainingAdditionalParameters[parameter.Key] >= 0);
                                }
                                if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                    newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                                }
                                if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                    newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                                }
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = entry;
                                if (debugOutputTransitions) {
                                    ModelCore.UI.WriteLine($"Transferred {concreteCohort.Data.Biomass} biomass from {speciesCohorts.Species.Name} to {targetSpecies.Name}");
                                }
                            }
                        }
                        //push remaining biomass to original species cohort
                        if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                            newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                        }
                        if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                        }
                        var entry_ = newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age];
                        entry_.biomass += concreteCohort.Data.Biomass - totalBiomassAccountedFor;
                        entry_.additionalParameters = remainingAdditionalParameters;
                        newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = entry_;
                    }
                }

                //rewrite SiteCohorts() regardless of changes
                //TODO: Create a clone of SiteCohorts minus the cohortData
                //seemingly not necessary though
                var newSiteCohorts = new SiteCohorts();
                foreach (var species in newSiteCohortsDictionary) {
                    foreach (var cohort in species.Value) {
                        if (cohort.Value.biomass > 0) {
                            ExpandoObject additionalParameters = new ExpandoObject();
                            IDictionary<string, object> additionalParametersDictionary = (IDictionary<string, object>)additionalParameters;
                            foreach (var parameter in cohort.Value.additionalParameters) {
                                Console.WriteLine($"2Parameter: {parameter.Key}, Value: {parameter.Value}");
                                additionalParametersDictionary[parameter.Key] = parameter.Value;
                            }
                            newSiteCohorts.AddNewCohort(species.Key, cohort.Key, cohort.Value.biomass, additionalParameters);
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
            ModelCore.UI.WriteLine($"Finished proportioning and rewriting SiteCohorts for all sites: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            globalTimer.Stop();
            ModelCore.UI.WriteLine($"DiseaseProgression timestep took: {globalTimer.ElapsedMilliseconds} ms");
        }
        private static void ReplaceAge1InfectedWithHealthy(IEnumerable<ActiveSite> sites, IInputParameters parameters) {
            Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>>();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                SiteCohorts siteCohorts = SiteVars.Cohorts[site];
                foreach (ISpeciesCohorts speciesCohorts in siteCohorts) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    foreach (ICohort cohort in concreteSpeciesCohorts) {
                        Cohort concreteCohort = (Cohort)cohort;
                        ISpecies addAsSpecies;
                        if (concreteCohort.Data.Age == 1) {
                            ISpecies designatedHealthySpecies = parameters.GetDesignatedHealthySpecies(speciesCohorts.Species);
                            if (designatedHealthySpecies == null) {
                                addAsSpecies = speciesCohorts.Species;
                            } else {
                                addAsSpecies = designatedHealthySpecies;
                            }
                        } else {
                            addAsSpecies = speciesCohorts.Species;
                        }
                        if (!newSiteCohortsDictionary.ContainsKey(addAsSpecies)) {
                            newSiteCohortsDictionary[addAsSpecies] = new Dictionary<ushort, (int biomass, Dictionary<string, int> additionalParameters)>();
                        }
                        if (!newSiteCohortsDictionary[addAsSpecies].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[addAsSpecies][concreteCohort.Data.Age] = (0, new Dictionary<string, int>());
                        }
                        (int biomass, Dictionary<string, int> additionalParameters) entry = newSiteCohortsDictionary[addAsSpecies][concreteCohort.Data.Age];
                        entry.biomass += concreteCohort.Data.Biomass;
                        foreach (var parameter in concreteCohort.Data.AdditionalParameters) {
                            //Console.WriteLine($"3Parameter: {parameter.Key}, Value: {parameter.Value}");
                            if (!entry.additionalParameters.ContainsKey(parameter.Key)) {
                                entry.additionalParameters[parameter.Key] = 0;
                            }
                            entry.additionalParameters[parameter.Key] += (int)parameter.Value;
                        }
                        newSiteCohortsDictionary[addAsSpecies][concreteCohort.Data.Age] = entry;
                    }
                }

                SiteCohorts newSiteCohorts_ = new SiteCohorts();
                foreach (var species in newSiteCohortsDictionary) {
                    foreach (var cohort in species.Value) {
                        //Console.WriteLine($"NEW Cohort: age: {cohort.Key}, Biomass: {cohort.Value.biomass}");
                        if (cohort.Value.biomass > 0) {
                            //Console.WriteLine($"TEST Cohort: age: {cohort.Key}, Biomass: {cohort.Value.biomass}");
                            ExpandoObject additionalParameters = new ExpandoObject();
                            IDictionary<string, object> additionalParametersDictionary = (IDictionary<string, object>)additionalParameters;
                            foreach (var parameter in cohort.Value.additionalParameters) {
                                //Console.WriteLine($"4Parameter: {parameter.Key}, Value: {parameter.Value}");
                                additionalParametersDictionary[parameter.Key] = parameter.Value;
                            }
                            newSiteCohorts_.AddNewCohort(species.Key, cohort.Key, cohort.Value.biomass, additionalParameters);
                        }
                    }
                }
                foreach (ISpeciesCohorts speciesCohorts in newSiteCohorts_) {
                    SpeciesCohorts concreteSpeciesCohorts = (SpeciesCohorts)speciesCohorts;
                    concreteSpeciesCohorts.UpdateMaturePresent();
                }
                SiteVars.Cohorts[site] = newSiteCohorts_;
                foreach (var data in newSiteCohortsDictionary) {
                    data.Value.Clear();
                }
            }
        }
        private static (bool[] sitesForProportioning,
                        List<int> healthySitesListIndices,
                        List<int> infectedSitesListIndices,
                        List<int> ignoredSitesListIndices) 
        InfectionStateDetection(IEnumerable<ActiveSite> sites, IInputParameters parameters, int landscapeX, int landscapeSize) {
            bool[] sitesForProportioning = new bool[landscapeSize];
            List<(int x, int y)> healthySitesList = new List<(int x, int y)>();
            List<(int x, int y)> infectedSitesList = new List<(int x, int y)>();
            List<(int x, int y)> ignoredSitesList = new List<(int x, int y)>();
            List<int> healthySitesListIndices = new List<int>();
            List<int> infectedSitesListIndices = new List<int>();
            List<int> ignoredSitesListIndices = new List<int>();
            Color[] colors = new Color[landscapeSize];
            foreach (ActiveSite site in sites) {
                int healthyBiomass = 0;
                int infectedBiomass = 0;
                int ignoredBiomass = 0;
                bool containsHealthySpecies = false;
                bool containsInfectedSpecies = false;
                foreach (ISpeciesCohorts speciesCohorts in SiteVars.Cohorts[site]) {
                    ISpecies designatedHealthySpecies = parameters.GetDesignatedHealthySpecies(speciesCohorts.Species);
                    //Console.WriteLine($"Looking at species: {speciesCohorts.Species.Name}{(designatedHealthySpecies != null ? $", it's designated healthy species is: {designatedHealthySpecies.Name}" : "")}");
                    if (designatedHealthySpecies != null && speciesCohorts.Species == designatedHealthySpecies) {
                        containsHealthySpecies = true;
                        foreach (ICohort cohort in speciesCohorts) {
                            healthyBiomass += cohort.Data.Biomass;
                        }
                    } else if (designatedHealthySpecies != null && parameters.TransitionMatrixContainsSpecies(speciesCohorts.Species)) {
                        containsInfectedSpecies = true;
                        foreach (ICohort cohort in speciesCohorts) {
                            infectedBiomass += cohort.Data.Biomass;
                        }
                    } else {
                        foreach (ICohort cohort in speciesCohorts) {
                            ignoredBiomass += cohort.Data.Biomass;
                        }
                    }
                }
                int totalBiomass = healthyBiomass + infectedBiomass + ignoredBiomass;
                byte redIntensity = (byte)Math.Round(((double)infectedBiomass / (double)totalBiomass) * 255.0);
                byte greenIntensity = (byte)Math.Round(((double)healthyBiomass / (double)totalBiomass) * 255.0);
                byte blueIntensity = (byte)Math.Round(((double)ignoredBiomass / (double)totalBiomass) * 255.0);
                Color color = Color.FromArgb(redIntensity, greenIntensity, blueIntensity);
                Location siteLocation = site.Location;
                int index = CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX);
                colors[index] = color;
                if (containsHealthySpecies && !containsInfectedSpecies) {
                    healthySitesList.Add((siteLocation.Column, siteLocation.Row));
                    healthySitesListIndices.Add(index);
                } else if (containsInfectedSpecies) {
                    infectedSitesList.Add((siteLocation.Column, siteLocation.Row));
                    infectedSitesListIndices.Add(index);
                    sitesForProportioning[index] = true;
                } else {
                    ignoredSitesList.Add((siteLocation.Column, siteLocation.Row));
                    ignoredSitesListIndices.Add(index);
                }
            }

            {
                Stopwatch outputStopwatch = new Stopwatch();
                outputStopwatch.Start();
                Task.Run(() => {
                    
                    try {
                        string outputPath = $"./infection_timeline_multi/infection_multi_state_{modelCore.CurrentTime}.png";
                        GenerateMultiStateBitmap(outputPath, colors);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting multi-state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            {
                Stopwatch outputStopwatch = new Stopwatch();
                outputStopwatch.Start();
                Task.Run(() => {
                    
                    try {
                        string outputPath = $"./overall_timeline/overall_state_{modelCore.CurrentTime}.png";
                        GenerateOverallStateBitmap(outputPath, colors);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting overall state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            {
                Task.Run(() => {
                    ModelCore.UI.WriteLine($"Healthy sites: {healthySitesList.Count}");
                    ModelCore.UI.WriteLine($"Infected sites: {infectedSitesList.Count}");
                    ModelCore.UI.WriteLine($"Ignored sites: {ignoredSitesList.Count}");
                    ModelCore.UI.WriteLine($"Newly infected sites: {infectedSitesList.Count - infectedSitesList.Count}");
                    
                    Stopwatch outputStopwatch = new Stopwatch();
                    outputStopwatch.Start();
                    try {
                        string outputPath = $"./infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                        GenerateInfectionStateBitmap(outputPath, healthySitesList, infectedSitesList, ignoredSitesList);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting infection state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            return (sitesForProportioning, healthySitesListIndices, infectedSitesListIndices, ignoredSitesListIndices);
        }
        public override void AddCohortData() { return; }
    }
}
