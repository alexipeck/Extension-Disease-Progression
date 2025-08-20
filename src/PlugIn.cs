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
            //bool debugDisableDiseaseProgressionKill = false;
            //bool debugOnlyOneTransferPerSitePerTimestep = false;
            bool debugOutputTransitions = false;
            bool debugDumpSiteInformation = false;
            bool infectionStatusOutput = true;
            //bool disableDispersal = false;
            ////////
            
            IEnumerable<ActiveSite> sites = ModelCore.Landscape.ActiveSites;

            ////////////////////
            // stores literal site positions of either sites which only contain the healthy species
            // specified within the matrix or sites which contain one of the infected variants
            HashSet<(int x, int y)> healthySites = new HashSet<(int x, int y)>();
            HashSet<(int x, int y)> infectedSites = new HashSet<(int x, int y)>();
            HashSet<(int x, int y)> ignoredSites = new HashSet<(int x, int y)>();

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
                    healthySites.Add((siteLocation.Row, siteLocation.Column));
                } else if (containsInfectedSpecies) {
                    infectedSites.Add((siteLocation.Row, siteLocation.Column));
                } else if (infectionStatusOutput) {
                    ignoredSites.Add((siteLocation.Row, siteLocation.Column));
                }
            }

            if (infectionStatusOutput) {
                string outputPath = $"./infection_timeline/infection_state_{modelCore.CurrentTime}.png";
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath));
                var landscapeDimensions = PlugIn.ModelCore.Landscape.Dimensions;
                (int landscapeX, int landscapeY) = (landscapeDimensions.Rows, landscapeDimensions.Columns);
                Color healthyColor = Color.Green;
                Color infectedColor = Color.Red;
                Color ignoredColor = Color.Blue;
                Bitmap bitmap = new Bitmap(landscapeX, landscapeY, PixelFormat.Format32bppArgb);
                foreach ((int x, int y) in healthySites) {
                    bitmap.SetPixel(x - 1, y - 1, healthyColor);
                }
                foreach ((int x, int y) in infectedSites) {
                    bitmap.SetPixel(x - 1, y - 1, infectedColor);
                }
                foreach ((int x, int y) in ignoredSites) {
                    bitmap.SetPixel(x - 1, y - 1, ignoredColor);
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }

            // compute relative site positions
            // compute cumulative probability of infection
            // TODO: This method may not agree with the data we have for infection
            //       which I assume just tells us the probability of a cohort becoming infected
            //       rather than being the probabilty of any infected cohort infecting another
            // Assuming that every infected site can infect any healthy site, I should be able
            // to do a nested loop for healthy->infected  and store the relative positions
            // then hold an accumilated probability for every healthy site, performing one RNG per healthy
            // site to determine whether it becomes infected.
            foreach ((int x, int y) healthySite in healthySites) {
                double cumulativeDispersalProbability = 0.0;
                foreach ((int x, int y) infectedSite in infectedSites) {
                    //TODO: Ensure that the index offset is the healthy site relative
                    //      to the infected siteas the infected site is the source.
                    (int x, int y) relativeGridOffset = SiteVars.CalculateRelativeGridOffset(infectedSite.x, infectedSite.y, healthySite.x, healthySite.y);
                    double dispersalProbability = SiteVars.GetDispersalProbability(relativeGridOffset.x, relativeGridOffset.y);
                    Debug.Assert(dispersalProbability >= 0.0 && dispersalProbability <= 1.0);
                    cumulativeDispersalProbability += dispersalProbability;
                }
                if (cumulativeDispersalProbability > 1.0) {
                    cumulativeDispersalProbability = 1.0;
                }
                if (cumulativeDispersalProbability == 0.0) {
                    continue;
                }
                Random rand = new Random();
                double random = rand.NextDouble();
                if (random <= cumulativeDispersalProbability) {
                    infectedSites.Add(healthySite);
                }
            }
            healthySites.Clear();
            ///////////////////
            
            // Species string to ISpecies lookup
            Dictionary<string, ISpecies> speciesNameToISpecies = new Dictionary<string, ISpecies>();
            foreach (var species in ModelCore.Species) {
                speciesNameToISpecies[species.Name] = species;
            }
            
            Dictionary<ISpecies, Dictionary<ushort, int>> newSiteCohortsDictionary = new Dictionary<ISpecies, Dictionary<ushort, int>>();
            foreach (ActiveSite site in sites) {
                Location siteLocation = site.Location;
                if (healthySites.Contains((siteLocation.Row, siteLocation.Column))) {
                    continue;
                } else if (!infectedSites.Contains((siteLocation.Row, siteLocation.Column))) {
                    continue;
                }
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
                        
                        foreach ((string species, double proportion) in transitionDistribution) {
                            //null case is the no change case within the matrix accounting for either
                            //the user specified proportion in the case of all proportions for a line
                            //adding up to 1.0, in all other cases, the null case equals the user specified
                            //proportion + the remaining proportion
                            if (species != null) {
                                int transfer = (int)(concreteCohort.Data.Biomass * proportion);
                                totalBiomassAccountedFor += transfer;
                                if (species.ToUpper() == "DEAD") {
                                    //there is a disconnect in the calculated biomass sent to the decomposition pools
                                    //because their math is based around division not multiplication, this increases the
                                    //error accumulation which is hard to account for because they don't end in a final biomass
                                    //value to be removed, the proportion of the biomass is used to calculate the amount of wood and foliar biomass
                                    //the error accumulation here will be the same as is mentioned for totalBiomassAccountedFor above.
                                    Cohort.CohortMortality(concreteSpeciesCohorts, concreteCohort, site, type, (float)proportion);
                                    if (debugOutputTransitions) {
                                        ModelCore.UI.WriteLine($"Transitioned to dead: Age: {concreteCohort.Data.Age}, Biomass: {concreteCohort.Data.Biomass}, Species: {speciesCohorts.Species.Name}");
                                    }
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
        }
        public override void AddCohortData() { return; }
    }
}
