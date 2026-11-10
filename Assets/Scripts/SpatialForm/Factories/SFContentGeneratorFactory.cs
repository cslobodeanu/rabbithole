using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialForms.Factories
{
    public abstract class SFContentGeneratorFactory : MonoBehaviour
    {
        // Optional target document that the generator can populate incrementally.
        public SFDocument TargetDocument { get; set; }

        public abstract Task<SFDocument> GenerateContentAsync(CancellationToken cancellationToken = default);
    }
}
