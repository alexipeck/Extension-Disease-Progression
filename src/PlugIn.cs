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
            SiteVars.Initialize(ModelCore, parameters);
            foreach (ISpecies speciesName in parameters.SpeciesTransitionMatrix.Keys) {
                ModelCore.UI.WriteLine($"{speciesName.Name}");
            }
            ModelCore.UI.WriteLine("");
            Timestep = parameters.Timestep;
            
            string[] pathsToEmpty = new string[] { "./infection_timeline", "./shi_timeline", "./shim_timeline", "./shim_normalized_timeline" };
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
            ModelCore.UI.WriteLine("Running disease progression");
            //////// DEBUG PARAMETERS
            bool debugOutputTransitions = false;
            bool debugDumpSiteInformation = false;
            //TODO: Image generation gets started in another thread
            //      I need a boolean check to ensure that the thread has stopped
            //      The thread is needed as is can save something like 7 steps per timestep
            bool debugOutputInfectionStateCounts = true;
            ////////
            
            int dispersalProbabilityMatrixWidth = DispersalProbabilityMatrixWidth;
            int dispersalProbabilityMatrixHeight = DispersalProbabilityMatrixHeight;
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;
            (int x, int y) worstCaseMaximumUniformDispersalDistance = GetWorstCaseMaximumUniformDispersalDistance();
            ISpecies derivedHealthySpecies = parameters.DerivedHealthySpecies;

            int landscapeX = LandscapeDimensions.x;
            int landscapeY = LandscapeDimensions.y;
            int landscapeSize = landscapeX * landscapeY;

            ////////Resprouting TODO: REWORK
            int[] resproutLifetime = ResproutLifetime;
            
            bool[] willResprout = new bool[landscapeSize];
            for (int x = 0; x < landscapeX; x++) {
                for (int y = 0; y < landscapeY; y++) {
                    int index = CalculateCoordinatesToIndex(x, y, landscapeX);
                    if (resproutLifetime[index] > 0) {
                        Random rand = new Random();
                        double random = rand.NextDouble();
                        if (random <= 0.15) willResprout[CalculateCoordinatesToIndex(x, y, landscapeX)] = true;
                    }
                }
            }
            DecrementResproutLifetimes();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                if (!willResprout[CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX)]) continue;
                Reproduction.AddNewCohort(derivedHealthySpecies, site, "resprout", 0);
            }
            ////////

            (bool[] sitesForProportioning, 
             List<(int x, int y)> healthySitesList, 
             List<(int x, int y)> infectedSitesList, 
             List<(int x, int y)> ignoredSitesList,
             List<int> healthySitesListIndices,
             List<int> infectedSitesListIndices,
             List<int> ignoredSitesListIndices) = 
                InfectionStateDetection(sites, parameters, landscapeX, landscapeSize);

            if (Timestep == 1) {
                //set default probabilities from infection state
                SetDefaultProbabilities(healthySitesListIndices, infectedSitesListIndices);
            } else {
                //modify probabilities based on their difference from previous timestep
                //if a site which was infected last timestep no longer is, set it back to 1 for susceptible, 0 for the others
                //I'm unsure if I need to track when a healthy becomes infected because it's handled only within this process
                //do we want to say that when a site is infected, that it's probability should be reset back to 1
                //or do we leave it to dynamically change based existing math?
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            
            double[] SHIM = new double[landscapeSize];

            ////////calculate SHI & SHIM
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
            
            ExportBitmap(SHIM, "./shim_timeline/shim_state", "SHIM");
            
            double SHIMMean = SHIMSum / ModelCore.Landscape.ActiveSiteCount;
            for (int i = 0; i < landscapeSize; i++) {
                //TODO: Consider adding a branch to skip if SHIM[i] isn't more
                //      than 0 but mathematically it's fine unless SHIMMean is 0
                SHIM[i] /= SHIMMean;
            }

            
            ExportBitmap(SHIM, "./shim_normalized_timeline/shim_normalized_state", "SHI Normalized");

            //////// Force of Infection
            double[] FOI = CalculateForceOfInfection(landscapeX, landscapeSize, SHIM);
            ExportBitmap(FOI, "./foi_timeline/foi_state", "FOI");
            ////////
            
            //int numberOfInfectedSitesBeforeDispersal = infectedSitesList.Count;

            // compute relative site positions
            // compute cumulative probability of infection
            // TODO: This method may not agree with the data we have for infection
            //       which I assume just tells us the probability of a cohort becoming infected
            //       rather than being the probabilty of any infected cohort infecting another
            // Assuming that every infected site can infect any healthy site, I should be able
            // to do a nested loop for healthy->infected  and store the relative positions
            // then hold an accumilated probability for every healthy site, performing one RNG per healthy
            // site to determine whether it becomes infected.
            Stopwatch stopwatch1 = new Stopwatch();
            stopwatch1.Start();
            List<(int x, int y, double cumulativeDispersalProbability)> healthySiteCumulativeDispersalProbabilities = new List<(int x, int y, double cumulativeDispersalProbability)>();
            foreach ((int x, int y) healthySite in healthySitesList) {
                double cumulativeDispersalProbability = 0.0;
                foreach ((int x, int y) infectedSite in infectedSitesList) {
                    (int x, int y) relativeGridOffset = CalculateRelativeGridOffset(infectedSite.x, infectedSite.y, healthySite.x, healthySite.y);
                    (int x, int y) canonicalizedRelativeGridOffset = CanonicalizeToHalfQuadrant(relativeGridOffset.x, relativeGridOffset.y);
                    if (canonicalizedRelativeGridOffset.x >= dispersalProbabilityMatrixWidth || canonicalizedRelativeGridOffset.y >= dispersalProbabilityMatrixHeight) continue;
                    double dispersalProbability = GetDispersalProbability(CalculateCoordinatesToIndex(canonicalizedRelativeGridOffset.x, canonicalizedRelativeGridOffset.y, dispersalProbabilityMatrixWidth));
                    cumulativeDispersalProbability += dispersalProbability;
                }
                if (cumulativeDispersalProbability == 0.0) continue;
                healthySiteCumulativeDispersalProbabilities.Add((healthySite.x, healthySite.y, cumulativeDispersalProbability));
            }
            foreach ((int x, int y, double cumulativeDispersalProbability) healthySite in healthySiteCumulativeDispersalProbabilities) {
                Random rand = new Random();
                double random = rand.NextDouble();
                if (random <= healthySite.cumulativeDispersalProbability) {
                    infectedSitesList.Add((healthySite.x, healthySite.y));
                    sitesForProportioning[CalculateCoordinatesToIndex(healthySite.x - 1, healthySite.y - 1, landscapeX)] = true;
                }
            }
            stopwatch1.Stop();
            ModelCore.UI.WriteLine($"TIMING: {stopwatch1.ElapsedMilliseconds} ms");
            
            if (debugOutputInfectionStateCounts) {
                ModelCore.UI.WriteLine($"Healthy sites: {healthySitesList.Count}");
                ModelCore.UI.WriteLine($"Infected sites: {infectedSitesList.Count}");
                ModelCore.UI.WriteLine($"Ignored sites: {ignoredSitesList.Count}");
                ModelCore.UI.WriteLine($"Newly infected sites: {infectedSitesList.Count - infectedSitesList.Count}");
            }
            healthySitesList.Clear();
            ModelCore.UI.WriteLine($"Finished determining which sites are newly infected: {stopwatch.ElapsedMilliseconds} ms");
            ///////////////////
            
            Dictionary<ISpecies, Dictionary<ushort, int>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, int>>();
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

                        //process entry through matrix
                        var transitionDistribution = parameters.GetTransitionMatrixDistribution(speciesCohorts.Species);

                        //no transition will occur
                        if (transitionDistribution == null) {
                            if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                                newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, int>();
                            }
                            if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                                newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = 0;
                            }
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] += concreteCohort.Data.Biomass;
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
                                    Cohort.CohortMortality(concreteSpeciesCohorts, concreteCohort, site, type, (float)proportion);
                                    if (debugOutputTransitions) {
                                        ModelCore.UI.WriteLine($"Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                                    }
                                    AddResproutLifetime(siteLocation.Row - 1, siteLocation.Column - 1);
                                    continue; //short-circuit
                                }

                                //push biomass to target species cohort
                                if (!newSiteCohortsDictionary.ContainsKey(targetSpecies)) {
                                    newSiteCohortsDictionary[targetSpecies] = new Dictionary<ushort, int>();
                                }
                                if (!newSiteCohortsDictionary[targetSpecies].ContainsKey(concreteCohort.Data.Age)) {
                                    newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] = 0;
                                }
                                newSiteCohortsDictionary[targetSpecies][concreteCohort.Data.Age] += transfer;
                                if (debugOutputTransitions) {
                                    ModelCore.UI.WriteLine($"Transferred {concreteCohort.Data.Biomass} biomass from {speciesCohorts.Species.Name} to {targetSpecies.Name}");
                                }
                            }
                        }
                        //push remaining biomass to original species cohort
                        if (!newSiteCohortsDictionary.ContainsKey(speciesCohorts.Species)) {
                            newSiteCohortsDictionary[speciesCohorts.Species] = new Dictionary<ushort, int>();
                        }
                        if (!newSiteCohortsDictionary[speciesCohorts.Species].ContainsKey(concreteCohort.Data.Age)) {
                            newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] = 0;
                        }
                        newSiteCohortsDictionary[speciesCohorts.Species][concreteCohort.Data.Age] += concreteCohort.Data.Biomass - totalBiomassAccountedFor;
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
            stopwatch.Stop();
            ModelCore.UI.WriteLine($"Finished proportioning all sites: {stopwatch.ElapsedMilliseconds} ms");
        }
        private static (bool[] sitesForProportioning, 
                        List<(int x, int y)> healthySitesList, 
                        List<(int x, int y)> infectedSitesList, 
                        List<(int x, int y)> ignoredSitesList,
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
            foreach (ActiveSite site in sites) {
                bool containsHealthySpecies = false;
                bool containsInfectedSpecies = false;
                foreach (ISpeciesCohorts speciesCohorts in SiteVars.Cohorts[site]) {
                    if (speciesCohorts.Species == parameters.DerivedHealthySpecies) {
                        containsHealthySpecies = true;
                    } else if (parameters.TransitionMatrixContainsSpecies(speciesCohorts.Species)) {
                        containsInfectedSpecies = true;
                    }
                }
                Location siteLocation = site.Location;
                int index = CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX);
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
                List<(int x, int y)> healthySitesListCopy = new List<(int x, int y)>(healthySitesList);
                List<(int x, int y)> infectedSitesListCopy = new List<(int x, int y)>(infectedSitesList);
                List<(int x, int y)> ignoredSitesListCopy = new List<(int x, int y)>(ignoredSitesList);
                
                Task.Run(() => {
                    Stopwatch outputStopwatch = new Stopwatch();
                    outputStopwatch.Start();
                    try {
                        string outputPath = $"./infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                        GenerateInfectionStateBitmap(outputPath, healthySitesListCopy, infectedSitesListCopy, ignoredSitesListCopy);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting infection state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }

            return (sitesForProportioning, healthySitesList, infectedSitesList, ignoredSitesList,
                    healthySitesListIndices, infectedSitesListIndices, ignoredSitesListIndices);
        }
        public override void AddCohortData() { return; }
    }
}
