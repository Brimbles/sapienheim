using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// A workshop: the usual starter shelter for a workbench. Two floor pieces (4 x 2 m), four walls (two across the
    /// back, one each side, with gable triangles on top), the front open, two sloping roof pieces with their low edge
    /// on the back wall rising to 3 m over the open front, and the workbench against the back wall under one roof
    /// piece (under the seam between two, vanilla's ray up for "under a roof" slips through). A standing torch inside
    /// the front corner if he has the resin for it. About 32 wood. Built through the blueprint path (site, levelling,
    /// workbench first, then bottom up).
    ///
    /// Local frame as a blueprint's: x along the front, z from the open front (-) to the back wall (+), y up from the
    /// floor surface. Geometry as in HutTemplate: a floor's surface is at its pivot; walls are 2 x 2 m centred on
    /// their pivot; wood_roof spans 2 x 2 m, its low edge (local +z) at the pivot's height, rising 1 m to local -z.
    /// </summary>
    internal static class WorkshopTemplate
    {
        public const string Torch = "piece_groundtorch_wood";

        public static Blueprints.Blueprint Create(bool torch)
        {
            var bp = new Blueprints.Blueprint { Name = "workshop" };
            void Add(string piece, float x, float y, float z, float yaw) =>
                bp.Pieces.Add((piece, new Vector3(x, y, z), Quaternion.Euler(0f, yaw, 0f)));

            Add("piece_workbench", 1f, 0f, 0.45f, 180f); // against the back wall, on the right, facing out
            Add("wood_floor", -1f, 0f, 0f, 0f);
            Add("wood_floor", 1f, 0f, 0f, 0f);
            Add("woodwall", -1f, 1f, 1f, 0f);
            Add("woodwall", 1f, 1f, 1f, 0f);
            Add("woodwall", -2f, 1f, 0f, 90f);
            Add("woodwall", 2f, 1f, 0f, 90f);
            // Gable triangles on the side walls, rising with the roof towards the front (as the hut's back gables):
            // without them the gaps let enough of vanilla's cover rays out to leave the bench at 59%, under the 70%.
            Add("wood_wall_roof_a", -2f, 2f, 0f, 90f);
            Add("wood_wall_roof_a", 2f, 2f, 0f, 90f);
            Add("wood_roof", -1f, 2f, 0f, 0f);
            Add("wood_roof", 1f, 2f, 0f, 0f);
            if (torch)
            {
                Add(Torch, -1.6f, 0f, -0.6f, 0f);
            }
            return bp;
        }
    }
}
