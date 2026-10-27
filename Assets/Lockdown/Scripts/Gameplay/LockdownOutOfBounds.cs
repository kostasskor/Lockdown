using UnityEngine;

namespace Lockdown
{
    /// <summary>
    /// OutOfBounds volume behind the boundary (Task 16, built by Lockdown/Build/Greybox in _Greybox/Boundary): a walker
    /// that enters it is put back at its last grounded spot inside the map. Backup only; the boundary walls, barriers
    /// and clips are what stop the walker (Lockdown/Review/Containment Test). Kept in builds.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class LockdownOutOfBounds : MonoBehaviour
    {
        void Reset() => GetComponent<Collider>().isTrigger = true;

        void OnTriggerEnter(Collider other)
        {
            var walker = other.GetComponent<LockdownWalker>();
            if (walker == null) return;
            walker.OutOfBoundsContacts++;
            walker.ReturnToSafeSpot();
        }

        void OnTriggerExit(Collider other)
        {
            var walker = other.GetComponent<LockdownWalker>();
            if (walker != null) walker.OutOfBoundsContacts = Mathf.Max(0, walker.OutOfBoundsContacts - 1);
        }

        void OnDrawGizmos()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.12f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.6f);
            Gizmos.DrawWireCube(box.center, box.size);
        }
    }
}
