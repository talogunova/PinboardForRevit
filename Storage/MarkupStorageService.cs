using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Pinboard.Storage
{
    /// <summary>
    /// Reads and writes a batch's pen markup ink data. One DataStorage
    /// element per batch, matched by name, same pattern as the schedule
    /// batches themselves.
    /// </summary>
    public static class MarkupStorageService
    {
        public static byte[] LoadMarkup(Document doc, string batchName)
        {
            Schema schema = MarkupSchemaProvider.GetOrCreateSchema();

            List<Element> storages = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .ToList();

            foreach (Element element in storages)
            {
                Entity entity = element.GetEntity(schema);
                if (!entity.IsValid())
                {
                    continue;
                }

                string name = entity.Get<string>(schema.GetField("BatchName"));
                if (name == batchName)
                {
                    IList<byte> data = entity.Get<IList<byte>>(schema.GetField("InkData"));
                    return data != null ? data.ToArray() : null;
                }
            }

            return null;
        }

        /// <summary>
        /// Must be called inside an open Transaction.
        /// </summary>
        public static void SaveMarkup(Document doc, string batchName, byte[] inkBytes)
        {
            Schema schema = MarkupSchemaProvider.GetOrCreateSchema();

            List<Element> storages = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .ToList();

            DataStorage target = null;
            foreach (Element element in storages)
            {
                Entity entity = element.GetEntity(schema);
                if (!entity.IsValid())
                {
                    continue;
                }

                string name = entity.Get<string>(schema.GetField("BatchName"));
                if (name == batchName)
                {
                    target = element as DataStorage;
                    break;
                }
            }

            if (target == null)
            {
                target = DataStorage.Create(doc);
            }

            Entity newEntity = new Entity(schema);
            newEntity.Set(schema.GetField("BatchName"), batchName);
            newEntity.Set<IList<byte>>(schema.GetField("InkData"), inkBytes.ToList());

            target.SetEntity(newEntity);
        }
    }
}
