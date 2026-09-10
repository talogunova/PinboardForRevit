using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Pinboard.Storage
{
    /// <summary>
    /// Defines the Extensible Storage schema used to save named batches of
    /// schedules inside the project file. Each batch is stored as its own
    /// DataStorage element carrying one entity of this schema, so the batch
    /// travels with the model and any teammate opening the file can load it.
    /// </summary>
    public static class PinboardSchemaProvider
    {
        // Fixed schema id, do not change once batches exist in real projects,
        // changing it would orphan any batches already saved under the old id.
        private static readonly Guid SchemaGuid = new Guid("3D9E4A1B-6C2F-4E7A-9B3D-1A2B3C4D5E6F");

        public static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null)
            {
                return existing;
            }

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("PinboardBatch");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("BatchName", typeof(string));
            builder.AddArrayField("ScheduleIds", typeof(ElementId));

            return builder.Finish();
        }
    }
}
