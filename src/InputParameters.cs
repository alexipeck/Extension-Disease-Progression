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
        double TransmissionRate { get; set; }
        SHIMode SHIMode { get; set; }
        Dictionary<ISpecies, SpeciesAgeMatrix> SpeciesTransitionAgeMatrix { get; set; }
        HashSet<ISpecies> InfectedSpeciesLookup { get; set; }
        Dictionary<ISpecies, HostIndex> SpeciesHostIndex { get; set; }
        (ISpecies, double)[] GetSpeciesTransitionAgeMatrixDistribution(ISpecies species, ushort age);
        ISpecies GetDesignatedHealthySpecies(ISpecies species);
        bool TransitionMatrixContainsSpecies(ISpecies species);
        ISpecies[] DesignatedHealthySpecies { get; set; }
        DistanceDispersalDecayKernel DistanceDispersalDecayKernel { get; set; }
        int DispersalMaxDistance { get; set; }
        IDistanceDispersalDecayKernel DistanceDispersalDecayKernelFunction { get; }
    }
    public class InputParameters
        : IInputParameters
    {
        private int timestep;
        private double transmissionRate;
        private Dictionary<ISpecies, SpeciesAgeMatrix> speciesTransitionAgeMatrix;
        private HashSet<ISpecies> infectedSpeciesLookup;
        private Dictionary<ISpecies, HostIndex> speciesHostIndex;
        private ISpecies[] designatedHealthySpecies;
        private DistanceDispersalDecayKernel distanceDispersalDecayKernel;
        private int dispersalMaxDistance;
        private IDistanceDispersalDecayKernel distanceDispersalDecayKernelFunction;
        private SHIMode shiMode;
        public Dictionary<ISpecies, SpeciesAgeMatrix> SpeciesTransitionAgeMatrix
        {
            get {
                return speciesTransitionAgeMatrix;
            }
            set {
                speciesTransitionAgeMatrix = value;
            }
        }
        public HashSet<ISpecies> InfectedSpeciesLookup
        {
            get {
                return infectedSpeciesLookup;
            }
            set {
                infectedSpeciesLookup = value;
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

        public ISpecies[] DesignatedHealthySpecies
        {
            get {
                return designatedHealthySpecies;
            }
            set {
                designatedHealthySpecies = value;
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

        public double TransmissionRate
        {
            get { return transmissionRate; }
            set { transmissionRate = value; }
        }

        public bool TransitionMatrixContainsSpecies(ISpecies species) {
            return infectedSpeciesLookup.Contains(species);
        }
        public (ISpecies, double)[] GetSpeciesTransitionAgeMatrixDistribution(ISpecies species, ushort age) {
            if (!speciesTransitionAgeMatrix.TryGetValue(species, out SpeciesAgeMatrix speciesAgeMatrix)) {
                return null;
            }
            return speciesAgeMatrix.GetDistribution(age);
        }
        public ISpecies GetDesignatedHealthySpecies(ISpecies species) {
            if (!speciesTransitionAgeMatrix.TryGetValue(species, out SpeciesAgeMatrix speciesAgeMatrix)) {
                return null;
            }
            return speciesAgeMatrix.DesignatedHealthySpecies();
        }
        
        public SHIMode SHIMode
        {
            get { return shiMode; }
            set { shiMode = value; }
        }
        
    }
}
