using Landis.Core;
using Landis.SpatialModeling;
using System.Collections.Generic;
using System.Linq;
using System.Dynamic;
using Landis.Library.UniversalCohorts;
using System.Diagnostics;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using Landis.Library.Succession;

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
            
            string[] pathsToEmpty = new string[] { "./infection_timeline", "./shi_timeline", "./shi_normalized_timeline" };
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
            bool debugInfectionStatusOutput = true;
            bool debugOutputInfectionStateCounts = true;
            ////////
            
            int landscapeX = SiteVars.LandscapeDimensions.x;
            int landscapeY = SiteVars.LandscapeDimensions.y;
            int landscapeSize = landscapeX * landscapeY;
            int dispersalProbabilityMatrixWidth = SiteVars.DispersalProbabilityMatrixWidth;
            int dispersalProbabilityMatrixHeight = SiteVars.DispersalProbabilityMatrixHeight;
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;
            (int x, int y) worstCaseMaximumUniformDispersalDistance = SiteVars.GetWorstCaseMaximumUniformDispersalDistance();
            ISpecies derivedHealthySpecies = parameters.DerivedHealthySpecies;

            ////////Resprouting TODO: REWORK
            int[] resproutLifetime = SiteVars.ResproutLifetime;
            bool[] willResprout = new bool[landscapeSize];
            for (int x = 0; x < landscapeX; x++) {
                for (int y = 0; y < landscapeY; y++) {
                    int index = SiteVars.CalculateCoordinatesToIndex(x, y, landscapeX);
                    if (resproutLifetime[index] > 0) {
                        Random rand = new Random();
                        double random = rand.NextDouble();
                        if (random <= 0.15) willResprout[SiteVars.CalculateCoordinatesToIndex(x, y, landscapeX)] = true;
                    }
                }
            }
            SiteVars.DecrementResproutLifetimes();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                if (!willResprout[SiteVars.CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX)]) continue;
                Reproduction.AddNewCohort(derivedHealthySpecies, site, "resprout", 0);
            }
            ////////

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            
            double[] SHI = new double[landscapeSize];
            bool[] sitesForProportioning = new bool[landscapeSize];
            //TODO: Might be able to decommission these soon
            List<(int x, int y)> healthySitesList = new List<(int x, int y)>();
            List<(int x, int y)> infectedSitesList = new List<(int x, int y)>();
            List<(int x, int y)> ignoredSitesList = new List<(int x, int y)>();
            List<int> healthySitesListIndices = new List<int>();
            List<int> infectedSitesListIndices = new List<int>();
            List<int> ignoredSitesListIndices = new List<int>();

            ////////calculate SHI
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                SHI[SiteVars.CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX)] = (int)SiteVars.CalculateSiteHostIndex(site);
            }
            
            {
                double[] SHICopy = new double[landscapeSize];
                Array.Copy(SHI, SHICopy, landscapeSize);
                Task.Run(() => {
                    Stopwatch shiOutputStopwatch = new Stopwatch();
                    shiOutputStopwatch.Start();
                    try {
                        string outputPath = $"./shi_timeline/shi_state_{modelCore.CurrentTime}.png";
                        SiteVars.GenerateSHIStateBitmap(outputPath, SHICopy);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    shiOutputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting SHI state: {shiOutputStopwatch.ElapsedMilliseconds} ms");
                });
            }
            ////////
            
            double SHISum = SHI.Sum();
            double SHIMean = SHISum / landscapeSize;

            double[] SHIM = new double[landscapeSize];
            for (int i = 0; i < landscapeSize; i++) {
                //TODO: Add land type modifier (LTM) and summed disturbance modifiers
                SHIM[i] = SiteVars.CalculateSiteHostIndexModified(SHI[i], 0.0, 0.0);
            }

            double[] SHIMNormalized = new double[landscapeSize];

            for (int i = 0; i < landscapeSize; i++) {
                SHIMNormalized[i] = SHIM[i] / SHIMean;
            }

            {
                double[] SHINormalizedCopy = new double[landscapeSize];
                Array.Copy(SHIMNormalized, SHINormalizedCopy, landscapeSize);
                Task.Run(() => {
                    Stopwatch shiOutputStopwatch = new Stopwatch();
                    shiOutputStopwatch.Start();
                    try {
                        string outputPath = $"./shi_normalized_timeline/shi_normalized_state_{modelCore.CurrentTime}.png";
                        SiteVars.GenerateSHIStateBitmap(outputPath, SHINormalizedCopy);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    shiOutputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting SHI Normalized state: {shiOutputStopwatch.ElapsedMilliseconds} ms");
                });
            }
            
            // infection detection & adjustment pass
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
                int index = SiteVars.CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX);
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

            /* if (debugPerformTiming) {
                ModelCore.UI.WriteLine($"Infection detection finished: {stopwatch.ElapsedMilliseconds} ms");
            } */

            if (debugInfectionStatusOutput) {
                List<(int x, int y)> healthySitesListCopy = new List<(int x, int y)>(healthySitesList);
                List<(int x, int y)> infectedSitesListCopy = new List<(int x, int y)>(infectedSitesList);
                List<(int x, int y)> ignoredSitesListCopy = new List<(int x, int y)>(ignoredSitesList);
                
                Task.Run(() => {
                    Stopwatch outputStopwatch = new Stopwatch();
                    outputStopwatch.Start();
                    try {
                        string outputPath = $"./infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                        SiteVars.GenerateInfectionStateBitmap(outputPath, healthySitesListCopy, infectedSitesListCopy, ignoredSitesListCopy);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    outputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting infection state: {outputStopwatch.ElapsedMilliseconds} ms");
                });
            }


            //////// PRIMARY CALCULATION
            double[] FOI = new double[landscapeSize];
            for (int i = 0; i < landscapeSize; i++) {
                FOI[i] = 
                    /* Transmission & Weather Index * */
                    (SHISum - SHI[i])
                    * SHI[i]
                    /* * Source Infection Probability Index */
                    /* * Distance Decay Index */;
            }
            {
                double[] FOICopy = new double[landscapeSize];
                Array.Copy(FOI, FOICopy, landscapeSize);
                Task.Run(() => {
                    Stopwatch shiOutputStopwatch = new Stopwatch();
                    shiOutputStopwatch.Start();
                    try {
                        string outputPath = $"./foi_timeline/foi_state_{modelCore.CurrentTime}.png";
                        SiteVars.GenerateSHIStateBitmap(outputPath, FOICopy);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    shiOutputStopwatch.Stop();
                    ModelCore.UI.WriteLine($"      Finished outputting FOI state: {shiOutputStopwatch.ElapsedMilliseconds} ms");
                });
            }
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
                    (int x, int y) relativeGridOffset = SiteVars.CalculateRelativeGridOffset(infectedSite.x, infectedSite.y, healthySite.x, healthySite.y);
                    (int x, int y) canonicalizedRelativeGridOffset = SiteVars.CanonicalizeToHalfQuadrant(relativeGridOffset.x, relativeGridOffset.y);
                    if (canonicalizedRelativeGridOffset.x >= dispersalProbabilityMatrixWidth || canonicalizedRelativeGridOffset.y >= dispersalProbabilityMatrixHeight) continue;
                    double dispersalProbability = SiteVars.GetDispersalProbability(SiteVars.CalculateCoordinatesToIndex(canonicalizedRelativeGridOffset.x, canonicalizedRelativeGridOffset.y, dispersalProbabilityMatrixWidth));
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
                    sitesForProportioning[SiteVars.CalculateCoordinatesToIndex(healthySite.x - 1, healthySite.y - 1, landscapeX)] = true;
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
                if (!sitesForProportioning[SiteVars.CalculateCoordinatesToIndex(siteLocation.Column - 1, siteLocation.Row - 1, landscapeX)]) continue;
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
                                    SiteVars.AddResproutLifetime(siteLocation.Row - 1, siteLocation.Column - 1);
                                    continue; //short-circuit
                                }
                                //ISpecies targetSpecies = SiteVars.GetISpecies(species);

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



        public override void AddCohortData() { return; }
    }
}
