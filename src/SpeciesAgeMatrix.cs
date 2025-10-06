using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Landis.Core;
using Landis.Utilities;

namespace Landis.Extension.Disturbance.DiseaseProgression
{
    //enum InterpolationMethod {Linear/* ,Polynomial,Spline,Cubic,Logarithmic */}
    public enum MissingBelowRangeMethod {Error,Ignore}
    public enum MissingInRangeMethod {Error,AgeThreshold,LinearInterpolation}
    public enum MissingAboveRangeMethod {Error,UseOldest,KillAll,Ignore}
    public readonly struct SpeciesAgeMatrix
    {
        //private readonly int startOfAgeRange;
        //private readonly bool[] agesPresent;
        //private readonly int[] missingAges;
        private readonly ISpecies _species;
        private readonly ISpecies _designatedHealthySpecies;
        private readonly ushort _youngestAge;
        private readonly ushort _oldestAge;
        private readonly Dictionary<ushort, (ISpecies, double)[]> _ageTransitionMatrix;
        private readonly MissingBelowRangeMethod _missingBelowRangeMethod;
        private readonly MissingInRangeMethod _missingInRangeMethod;
        private readonly MissingAboveRangeMethod _missingAboveRangeMethod;

        public SpeciesAgeMatrix(ISpecies species,
        ISpecies designatedHealthySpecies,
        Dictionary<ushort, (ISpecies, double)[]> ageTransitionMatrix,
        MissingBelowRangeMethod missingBelowRangeMethod,
        MissingInRangeMethod missingInRangeMethod,
        MissingAboveRangeMethod missingAboveRangeMethod) {
            ushort youngestAge = ageTransitionMatrix.Keys.ToArray().Min();
            ushort oldestAge = ageTransitionMatrix.Keys.ToArray().Max();
            ////////validation
            //validate for MissingBelowRangeMethod
            switch (missingBelowRangeMethod) {
                case MissingBelowRangeMethod.Error:
                    if (youngestAge > 1) throw new InputValueException($"transition.data.{species.Name}", $"Configured to error on missing below range, lowest age must be 1.");
                    break;
                case MissingBelowRangeMethod.Ignore:
                    Console.WriteLine($"transition.data.{species.Name}: Configured to ignore missing below range.");
                    //validation isn't needed, it will just ignore values below the threshold missing at runtime
                    break;
            }
            
            //validate for MissingInRangeMethod
            switch (missingInRangeMethod) {
                case MissingInRangeMethod.Error:
                    if (oldestAge - ageTransitionMatrix.Count > youngestAge) throw new InputValueException($"transition.data.{species.Name}", $"Configured to error on missing in range, values are missing for some ages inside range.");
                    break;
                case MissingInRangeMethod.AgeThreshold:
                    List<ushort> allAgesInRange = Enumerable.Range(youngestAge, (oldestAge - youngestAge) + 1).Select(x => (ushort)x).ToList();
                    List<ushort> missingAges = allAgesInRange.Where(age => !ageTransitionMatrix.ContainsKey(age)).ToList();
                    foreach (ushort missingAge in missingAges) {
                        ushort sourceAge = (ushort)(missingAge - 1);
                        if (!ageTransitionMatrix.ContainsKey(sourceAge)) {
                            throw new InvalidOperationException($"Source age {sourceAge} not found for missing age {missingAge}. This indicates a logic error in the age threshold method.");
                        }
                        ageTransitionMatrix[missingAge] = ageTransitionMatrix[sourceAge];
                    }
                    break;
                case MissingInRangeMethod.LinearInterpolation:
                    throw new NotImplementedException();
                    //break;
            }

            //validate for MissingAboveRangeMethod
            switch (missingAboveRangeMethod) {
                case MissingAboveRangeMethod.Error:
                    //NOTE: I have set this to Longevity - 1 succession should age up and kill it before it ever hits this extension.
                    if (oldestAge < species.Longevity - 1) throw new InputValueException($"transition.data.{species.Name}", $"Configured to error on data missing up to species longevity.");
                    break;
                case MissingAboveRangeMethod.UseOldest:
                    Console.WriteLine($"transition.data.{species.Name}: Configured to use oldest age data when missing above range.");
                    break;
                case MissingAboveRangeMethod.KillAll:
                    Console.WriteLine($"transition.data.{species.Name}: Configured to kill of the cohort when missing above range.");
                    break;
                case MissingAboveRangeMethod.Ignore:
                    Console.WriteLine($"transition.data.{species.Name}: Configured to ignore missing above range.");
                    break;
            }
            ////////

            //init
            Console.WriteLine($"Initializing species age matrix for species: {species.Name}, designated healthy species: {designatedHealthySpecies.Name}");
            _species = species;
            _designatedHealthySpecies = designatedHealthySpecies;
            _ageTransitionMatrix = ageTransitionMatrix;
            _youngestAge = youngestAge;
            _oldestAge = oldestAge;
            _missingBelowRangeMethod = missingBelowRangeMethod;
            _missingInRangeMethod = missingInRangeMethod;
            _missingAboveRangeMethod = missingAboveRangeMethod;
        }

        public ISpecies DesignatedHealthySpecies() {
            return _designatedHealthySpecies;
        }
        public (ISpecies, double)[] GetDistribution(ushort age) {
            if (_ageTransitionMatrix.TryGetValue(age, out (ISpecies, double)[] distribution)) {
                return distribution;
            }
            if (age < _youngestAge) {
                switch (_missingBelowRangeMethod) {
                    case MissingBelowRangeMethod.Ignore:
                        return null;
                    default:
                        throw new InvalidOperationException($"Attempted to get distribution for species {_species.Name} at age {age} but the age being below the youngest age of {_youngestAge} was not handled correctly during initialization, this indicates a logic error.");
                }
            } else if (age > _oldestAge) {
                switch (_missingAboveRangeMethod) {
                    case MissingAboveRangeMethod.UseOldest:
                        return _ageTransitionMatrix[_oldestAge];
                    case MissingAboveRangeMethod.KillAll:
                        return new (ISpecies, double)[] {(null, 1.0)};
                    case MissingAboveRangeMethod.Ignore:
                        return null;
                    default:
                        throw new InvalidOperationException($"Attempted to get distribution for species {_species.Name} at age {age} but the age being above the oldest age of {_oldestAge} was not handled correctly during initialization, this indicates a logic error.");
                }
            }

            throw new InvalidOperationException($"Attempted to get distribution for species {_species.Name} at age {age} but the age being below the youngest age of {_youngestAge} and above the oldest age of {_oldestAge} indicates that the age is within the range which should have been handled during initialization. This indicates a logic error.");
        }
    }
}