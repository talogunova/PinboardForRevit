using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Pinboard.Storage
{
    /// <summary>
    /// Defines the Extensible Storage schema used to save a batch's pen
    /// markups, separate from the PinboardBatch schema so adding markup
    /// support never touches or risks the existing saved batches.
    /// </summary>
    public static class MarkupSchemaProvider
    {
        private static readonly Guid SchemaGuid = new Guid("9F2E7A3B-4C1D-4E8F-BA2C-5D6E7F8A9B0C");

        public static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null)
            {
                return existing;
            }

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("PinboardMarkup");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("BatchName", typeof(string));
            builder.AddArrayField("InkData", typeof(byte));

            return builder.Finish();
        }
    }
}
