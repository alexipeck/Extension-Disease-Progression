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
            foreach (string speciesName in parameters.SpeciesTransitionMatrix.Keys) {
                ModelCore.UI.WriteLine($"{speciesName}");
            }
            ModelCore.UI.WriteLine("");
            Timestep = parameters.Timestep;
            
            //empty the infection timeline folder
            string infectionTimelinePath = "./infection_timeline";
            if (System.IO.Directory.Exists(infectionTimelinePath)) {
                System.IO.DirectoryInfo directory = new System.IO.DirectoryInfo(infectionTimelinePath);
                foreach (System.IO.FileInfo file in directory.GetFiles()) {
                    file.Delete();
                }
                ModelCore.UI.WriteLine($"Emptied infection timeline folder: {infectionTimelinePath}");
            }
            
            ModelCore.UI.WriteLine("Disease progression initialized");
        }

        //---------------------------------------------------------------------
        public override void Run()
        {
            ModelCore.UI.WriteLine("Running disease progression");
            ////////
            //DEBUG PARAMETERS
            bool debugOutputTransitions = false;
            bool debugDumpSiteInformation = false;
            bool debugInfectionStatusOutput = true;
            bool debugPerformTiming = true;
            bool debugOutputInfectionStateCounts = true;
            byte debugInfectionStatusOutputScaleFactor = 10;
            ////////
            
            Dimensions landscapeDimensions = ModelCore.Landscape.Dimensions;
            int worstCaseMaximumUniformDispersalDistance = SiteVars.GetWorstCaseMaximumUniformDispersalDistance();
            List<(int x, int y)> precalculatedDispersalDistanceOffsets = SiteVars.PrecalculatedDispersalDistanceOffsets;
            
            Stopwatch stopwatch = new Stopwatch();
            if (debugPerformTiming) {
                stopwatch.Start();
            }
            
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;

            ////////////////////
            // stores literal site positions of either sites which only contain the healthy species
            // specified within the matrix or sites which contain one of the infected variants
            bool[,] sitesForProportioning = new bool[landscapeDimensions.Columns, landscapeDimensions.Rows];
            List<(int x, int y)> healthySitesList = new List<(int x, int y)>();
            List<(int x, int y)> infectedSitesList = new List<(int x, int y)>();
            List<(int x, int y)> ignoredSitesList = new List<(int x, int y)>();

            
            // infection detection & adjustment pass
            foreach (ActiveSite site in sites) {
                bool containsHealthySpecies = false;
                bool containsInfectedSpecies = false;
                foreach (ISpeciesCohorts speciesCohorts in SiteVars.Cohorts[site]) {
                    if (speciesCohorts.Species.Name == parameters.DerivedHealthySpecies) {
                        containsHealthySpecies = true;
                    } else if (parameters.TransitionMatrixContainsSpecies(speciesCohorts.Species.Name)) {
                        containsInfectedSpecies = true;
                    }
                }
                Location siteLocation = site.Location;
                if (containsHealthySpecies && !containsInfectedSpecies) {
                    healthySitesList.Add((siteLocation.Column, siteLocation.Row));
                } else if (containsInfectedSpecies) {
                    infectedSitesList.Add((siteLocation.Column, siteLocation.Row));
                    sitesForProportioning[siteLocation.Column - 1, siteLocation.Row - 1] = true;
                } else {
                    ignoredSitesList.Add((siteLocation.Column, siteLocation.Row));
                }
            }

            if (debugPerformTiming) {
                ModelCore.UI.WriteLine($"Infection detection finished: {stopwatch.ElapsedMilliseconds} ms");
            }

            if (debugInfectionStatusOutput) {
                List<(int x, int y)> healthySitesListCopy = new List<(int x, int y)>(healthySitesList);
                List<(int x, int y)> infectedSitesListCopy = new List<(int x, int y)>(infectedSitesList);
                List<(int x, int y)> ignoredSitesListCopy = new List<(int x, int y)>(ignoredSitesList);
                
                Task.Run(() => {
                    Stopwatch outputStopwatch = new Stopwatch();
                    if (debugPerformTiming) {
                        outputStopwatch.Start();
                    }
                    try {
                        string outputPath = $"./infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath));
                        (int landscapeX, int landscapeY) = (landscapeDimensions.Rows, landscapeDimensions.Columns);
                        Color healthyColor = Color.Green;
                        Color infectedColor = Color.Red;
                        Color ignoredColor = Color.Blue;
                        byte scaleFactor = debugInfectionStatusOutputScaleFactor;
                        Bitmap bitmap = new Bitmap(landscapeX * scaleFactor, landscapeY * scaleFactor, PixelFormat.Format32bppArgb);
                        
                        foreach ((int x, int y) in healthySitesListCopy) {
                            int actualX = (x - 1) * scaleFactor;
                            int actualY = (y - 1) * scaleFactor;
                            for (int i = 0; i < scaleFactor; i++) {
                                for (int j = 0; j < scaleFactor; j++) {
                                    int pixelX = actualX + i;
                                    int pixelY = actualY + j;
                                    bitmap.SetPixel(pixelX, pixelY, healthyColor);
                                }
                            }
                        }
                        foreach ((int x, int y) in infectedSitesListCopy) {
                            int actualX = (x - 1) * scaleFactor;
                            int actualY = (y - 1) * scaleFactor;
                            for (int i = 0; i < scaleFactor; i++) {
                                for (int j = 0; j < scaleFactor; j++) {
                                    int pixelX = actualX + i;
                                    int pixelY = actualY + j;
                                    bitmap.SetPixel(pixelX, pixelY, infectedColor);
                                }
                            }
                        }
                        foreach ((int x, int y) in ignoredSitesListCopy) {
                            int actualX = (x - 1) * scaleFactor;
                            int actualY = (y - 1) * scaleFactor;
                            for (int i = 0; i < scaleFactor; i++) {
                                for (int j = 0; j < scaleFactor; j++) {
                                    int pixelX = actualX + i;
                                    int pixelY = actualY + j;
                                    bitmap.SetPixel(pixelX, pixelY, ignoredColor);
                                }
                            }
                        }
                        
                        bitmap.Save(outputPath, ImageFormat.Png);
                    }
                    catch (Exception ex) {
                        ModelCore.UI.WriteLine($"Debug bitmap generation failed: {ex.Message}");
                        throw;
                    }
                    if (debugPerformTiming) {
                        outputStopwatch.Stop();
                        ModelCore.UI.WriteLine($"      Finished outputting infection state: {outputStopwatch.ElapsedMilliseconds} ms");
                    }
                });
            }
            
            int numberOfInfectedSitesBeforeDispersal = infectedSitesList.Count;

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
                    if (canonicalizedRelativeGridOffset.x >= worstCaseMaximumUniformDispersalDistance || canonicalizedRelativeGridOffset.y >= worstCaseMaximumUniformDispersalDistance) continue;
                    double dispersalProbability = SiteVars.GetDispersalProbability(canonicalizedRelativeGridOffset.x, canonicalizedRelativeGridOffset.y);
                    //Debug.Assert(dispersalProbability >= 0.0 && dispersalProbability <= 1.0);
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
                    sitesForProportioning[healthySite.x - 1, healthySite.y - 1] = true;
                }
            }
            stopwatch1.Stop();
            ModelCore.UI.WriteLine($"TIMING: {stopwatch1.ElapsedMilliseconds} ms");
            
            if (debugOutputInfectionStateCounts) {
                ModelCore.UI.WriteLine($"Healthy sites: {healthySitesList.Count}");
                ModelCore.UI.WriteLine($"Infected sites: {infectedSitesList.Count}");
                ModelCore.UI.WriteLine($"Ignored sites: {ignoredSitesList.Count}");
                ModelCore.UI.WriteLine($"Newly infected sites: {infectedSitesList.Count - numberOfInfectedSitesBeforeDispersal}");
            }
            healthySitesList.Clear();
            if (debugPerformTiming) {
                ModelCore.UI.WriteLine($"Finished determining which sites are newly infected: {stopwatch.ElapsedMilliseconds} ms");
            }
            ///////////////////
            
            // Species string to ISpecies lookup
            Dictionary<string, ISpecies> speciesNameToISpecies = new Dictionary<string, ISpecies>();
            foreach (var species in ModelCore.Species) {
                speciesNameToISpecies[species.Name] = species;
            }
            
            Dictionary<ISpecies, Dictionary<ushort, int>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, int>>();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                if (!sitesForProportioning[siteLocation.Column - 1, siteLocation.Row - 1]) continue;
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
                        var transitionDistribution = parameters.GetTransitionMatrixDistribution(speciesCohorts.Species.Name);

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
                        
                        foreach ((string species, double proportion) in transitionDistribution) {
                            //null case is the no change case within the matrix accounting for either
                            //the user specified proportion in the case of all proportions for a line
                            //adding up to 1.0, in all other cases, the null case equals the user specified
                            //proportion + the remaining proportion
                            if (species != null) {
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
                                if (species.ToUpper() == "DEAD" || concreteCohort.Data.Biomass == 1) {
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
                                    SiteVars.AddResproutLifetime(siteLocation.Row, siteLocation.Column);
                                    continue; //short-circuit
                                }
                                ISpecies targetSpecies = speciesNameToISpecies[species];

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
            if (debugPerformTiming) {
                stopwatch.Stop();
                ModelCore.UI.WriteLine($"Finished proportioning all sites: {stopwatch.ElapsedMilliseconds} ms");
            }
        }



        public override void AddCohortData() { return; }
    }
}
