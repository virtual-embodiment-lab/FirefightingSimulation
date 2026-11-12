using UnityEngine;
using UnityEngine.VFX;

namespace Ignis
{
    /// <summary>Shared texture and world-space bounds for one house's static smoke collision geometry.</summary>
    public sealed class SmokeContainmentField : ScriptableObject
    {
        [SerializeField] private Texture3D distanceField;
        [SerializeField] private Vector3 minimum;
        [SerializeField] private Vector3 size;
        [SerializeField] private float voxelSize;

        public bool IsValid => distanceField != null && voxelSize > 0f &&
            size.x > 0f && size.y > 0f && size.z > 0f;

        /// <summary>Records a generated field and its lookup bounds.</summary>
        public void Initialize(Texture3D texture, Vector3 worldMinimum, Vector3 worldSize, float voxel)
        {
            distanceField = texture;
            minimum = worldMinimum;
            size = worldSize;
            voxelSize = voxel;
        }

        /// <summary>Binds the house's field before the effect starts emitting.</summary>
        public void Bind(VisualEffect effect)
        {
            effect.SetTexture("ContainmentField", distanceField);
            effect.SetVector3("ContainmentMinimum", minimum);
            effect.SetVector3("ContainmentSize", size);
            effect.SetFloat("ContainmentVoxel", voxelSize);
        }
    }
}
