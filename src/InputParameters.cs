using System.Collections.Generic;
using System;
using Landis.Utilities;
using Landis.Core;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    public readonly struct HostIndexEntry {
        public ushort Age { get; }
        public byte Score { get; }
        public HostIndexEntry(ushort age, byte score) {
            Age = age;
            Score = score;
        }
        public void Deconstruct(out ushort age, out byte score) {
            age = Age;
            score = Score;
        }
    }
    public readonly struct HostIndex {
        public HostIndexEntry Low { get; }
        public HostIndexEntry Medium { get; }
        public HostIndexEntry High { get; }
        public HostIndex(HostIndexEntry low, HostIndexEntry medium, HostIndexEntry high) {
            Low = low;
            Medium = medium;
            High = high;
        }
    }
    public enum DistanceDispersalDecayKernel { PowerLaw, NegativeExponent, SingleAnchoredPowerLaw, DoubleAnchoredPowerLaw };
    public enum SHIMode { Mean, Max };
    public interface IInputParameters
    {
        int Timestep {get;set;}
        Dictionary<ISpecies, List<(ISpecies, double)>> SpeciesTransitionMatrix { get; set; }
        Dictionary<ISpecies, HostIndex> SpeciesHostIndex { get; set; }
        List<(ISpecies, double)> GetTransitionMatrixDistribution(ISpecies species);
        bool TransitionMatrixContainsSpecies(ISpecies species);
        ISpecies DerivedHealthySpecies { get; set; }
        DistanceDispersalDecayKernel DistanceDispersalDecayKernel { get; set; }
        int DispersalMaxDistance { get; set; }
        IDistanceDispersalDecayKernel DistanceDispersalDecayKernelFunction { get; }
    }
    public class InputParameters
        : IInputParameters
    {
        private int timestep;
        private Dictionary<ISpecies, List<(ISpecies, double)>> speciesTransitionMatrix;
        private Dictionary<ISpecies, HostIndex> speciesHostIndex;
        private ISpecies derivedHealthySpecies;
        private DistanceDispersalDecayKernel distanceDispersalDecayKernel;
        private int dispersalMaxDistance;
        private IDistanceDispersalDecayKernel distanceDispersalDecayKernelFunction;
        public Dictionary<ISpecies, List<(ISpecies, double)>> SpeciesTransitionMatrix
        {
            get {
                return speciesTransitionMatrix;
            }
            set {
                speciesTransitionMatrix = value;
            }
        }
        public Dictionary<ISpecies, HostIndex> SpeciesHostIndex
        {
            get {
                return speciesHostIndex;
            }
            set {
                speciesHostIndex = value;
            }
        }

        public DistanceDispersalDecayKernel DistanceDispersalDecayKernel
        {
            get {
                return distanceDispersalDecayKernel;
            }
            set {
                distanceDispersalDecayKernel = value;
            }
        }
        public int DispersalMaxDistance
        {
            get {
                return dispersalMaxDistance;
            }
            set {
                dispersalMaxDistance = value;
            }
        }
        public IDistanceDispersalDecayKernel DistanceDispersalDecayKernelFunction
        {
            get { return distanceDispersalDecayKernelFunction; }
        }

        public ISpecies DerivedHealthySpecies
        {
            get {
                return derivedHealthySpecies;
            }
            set {
                derivedHealthySpecies = value;
            }
        }
        public void SetDistanceDispersalDecayKernelFunction(IDistanceDispersalDecayKernel k) { distanceDispersalDecayKernelFunction = k; }

        public InputParameters() {}
        public int Timestep
        {
            get {
                return timestep;
            }
            set {
                if (value < 0)
                        throw new InputValueException(value.ToString(),
                                                      "Value must be = or > 0.");
                timestep = value;
            }
        }

        public bool TransitionMatrixContainsSpecies(ISpecies species) {
            return speciesTransitionMatrix.ContainsKey(species);
        }
        public List<(ISpecies, double)> GetTransitionMatrixDistribution(ISpecies species) {
            if (!speciesTransitionMatrix.TryGetValue(species, out List<(ISpecies, double)> speciesTransitions)) {
                return null;
            }
            return speciesTransitions;
        }
        
    }
}
