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
            Log.Init();
            SiteVars.Initialize(ModelCore, parameters);
            Log.Info(LogType.General, "Species selected for disease progression:");
            foreach (ISpecies speciesName in parameters.SpeciesTransitionAgeMatrix.Keys) {
                Log.Info(LogType.General, $"{speciesName.Name}");
            }
            {
                foreach (var kvp in parameters.SpeciesTransitionAgeMatrix) {
                    var sp = kvp.Key;
                    var mat = kvp.Value;
                    var dict = (Dictionary<ushort, (ISpecies, double)[]>)kvp.Value.GetType().GetField("_ageTransitionMatrix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kvp.Value);
                    var ages = new List<ushort>(dict.Keys);
                    ages.Sort();
                    foreach (ushort age in ages) {
                        var dist = dict[age];
                        var parts = new List<string>();
                        foreach (var t in dist) {
                            parts.Add($"{(t.Item1 == null ? "DEAD" : t.Item1.Name)}={t.Item2}");
                        }
                        Log.Info(LogType.General, $"transition.data.{sp.Name}.{age}: {string.Join(", ", parts)}");
                    }
                }
            }
            Timestep = parameters.Timestep;
            
            string[] pathsToEmpty = new string[] { "./images/infection_timeline", "./images/shi_timeline", "./images/shim_timeline", "./images/shim_normalized_timeline", "./images/foi_timeline", "./images/foi_colourised_timeline", "./images/infection_timeline_multi", "./images/overall_timeline" };
            foreach (string path in pathsToEmpty) {
                if (System.IO.Directory.Exists(path)) {
                    System.IO.DirectoryInfo directory = new System.IO.DirectoryInfo(path);
                    foreach (System.IO.FileInfo file in directory.GetFiles()) {
                        file.Delete();
                    }
                    Log.Info(LogType.General, $"Emptied folder: {path}");
                }
            }

            {
                Log.Info(LogType.General, "Starting initial infection map application");
                bool[] initialInfectionMap = InitialInfectionMap;
                if (initialInfectionMap != null) {
                    int index = 0;
                    foreach (bool value in initialInfectionMap) {
                        if (value == true) {
                            Log.Info(LogType.General, $"Applying initial infection map to site at index: {index}");
                        }
                        index++;
                    }
                    ProportionSites(ModelCore.Landscape.ActiveSites, initialInfectionMap, LandscapeDimensions.x, type);
                }
                Log.Info(LogType.General, "Finished applying initial infection map");
            }
            
            Log.Info(LogType.General, "Disease progression initialized");
        }

        //---------------------------------------------------------------------
        public override void Run()
        {
            //DumpSiteInformation(ModelCore.Landscape.ActiveSites);
            Log.Info(LogType.General, "Running disease progression");
            //////// DEBUG PARAMETERS
            //bool debugOutputTransitions = false;
            //bool debugDumpSiteInformation = false;
            //TODO: Image generation gets started in another thread
            //      I need a boolean check to ensure that the thread has stopped
            //      The thread is needed as is can save something like 7 steps per timestep
            ////////
            Log.Info(LogType.General, $"Running timestep (marker for general): {ModelCore.CurrentTime}");
            Log.Info(LogType.Timing, $"Running timestep (marker for timing): {ModelCore.CurrentTime}");
            Log.Info(LogType.Transitions, $"Running timestep (marker for transitions): {ModelCore.CurrentTime}");
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
            Log.Info(LogType.General, $"Finished replacing age 1 infected with healthy: {stopwatch.ElapsedMilliseconds} ms");
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
            Log.Info(LogType.General, $"Finished resprouting: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ////////

            stopwatch.Start();
            (bool[] sitesForProportioning,
             List<int> healthySitesListIndices,
             List<int> infectedSitesListIndices,
             List<int> ignoredSitesListIndices) = 
                InfectionStateDetection(sites, parameters, landscapeX, landscapeSize);
            Log.Info(LogType.General, $"Finished infection state detection: {stopwatch.ElapsedMilliseconds} ms");
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
            Log.Info(LogType.General, $"Finished enforcing infected probability: {stopwatch.ElapsedMilliseconds} ms");
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
            Log.Info(LogType.General, $"Finished calculating SHI & SHIM: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(SHIM, "./images/shim_timeline/shim_state", "SHIM");
            
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
            Log.Info(LogType.General, $"Finished normalizing SHIM: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(SHIM, "./images/shim_normalized_timeline/shim_normalized_state", "SHI Normalized");

            stopwatch.Start();
            double[] FOI = CalculateForceOfInfection(landscapeSize, SHIM);
            Log.Info(LogType.General, $"Finished calculating FOI: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            ExportNumericalBitmap(FOI, "./images/foi_timeline/foi_state", "FOI");
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
            ExportIntensityBitmap(FOIScaled, "./images/foi_colourised_timeline/foi_colourised_state", "FOI Colourised");

            stopwatch.Start();
            foreach (int healthySiteIndex in healthySitesListIndices) {
                if (FOI[healthySiteIndex] == 0.0) continue;
                double random = rand.NextDouble();
                if (random <= FOI[healthySiteIndex]) {
                    sitesForProportioning[healthySiteIndex] = true;
                }
            }
            
            Log.Info(LogType.General, $"Finished determining which sites are newly infected: {stopwatch.ElapsedMilliseconds} ms");
            ///////////////////
            
            stopwatch.Start();
            ProportionSites(sites, sitesForProportioning, landscapeX, type);
            Log.Info(LogType.General, $"Finished proportioning and rewriting SiteCohorts for all sites: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            globalTimer.Stop();
            Log.Info(LogType.General, $"DiseaseProgression timestep took: {globalTimer.ElapsedMilliseconds} ms");
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
                        string outputPath = $"./images/infection_timeline_multi/infection_multi_state_{modelCore.CurrentTime}.png";
                        GenerateMultiStateBitmap(outputPath, colors);
                    }
                    catch (Exception ex) {
                        Log.Error(LogType.General, $"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    Log.Info(LogType.General, $"      Finished outputting multi-state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            {
                Stopwatch outputStopwatch = new Stopwatch();
                outputStopwatch.Start();
                Task.Run(() => {
                    
                    try {
                        string outputPath = $"./images/overall_timeline/overall_state_{modelCore.CurrentTime}.png";
                        GenerateOverallStateBitmap(outputPath, colors);
                    }
                    catch (Exception ex) {
                        Log.Error(LogType.General, $"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    Log.Info(LogType.General, $"      Finished outputting overall state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            {
                Task.Run(() => {
                    Log.Info(LogType.General, $"Healthy sites: {healthySitesList.Count}");
                    Log.Info(LogType.General, $"Infected sites: {infectedSitesList.Count}");
                    Log.Info(LogType.General, $"Ignored sites: {ignoredSitesList.Count}");
                    Log.Info(LogType.General, $"Newly infected sites: {infectedSitesList.Count - infectedSitesList.Count}");
                    
                    Stopwatch outputStopwatch = new Stopwatch();
                    outputStopwatch.Start();
                    try {
                        string outputPath = $"./images/infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                        GenerateInfectionStateBitmap(outputPath, healthySitesList, infectedSitesList, ignoredSitesList);
                    }
                    catch (Exception ex) {
                        Log.Error(LogType.General, $"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    Log.Info(LogType.General, $"      Finished outputting infection state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            return (sitesForProportioning, healthySitesListIndices, infectedSitesListIndices, ignoredSitesListIndices);
        }
        public override void AddCohortData() { return; }
    }
}
