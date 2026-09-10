using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Pinboard.Storage
{
    /// <summary>
    /// A saved batch, a name plus the schedule element ids that belong to it.
    /// </summary>
    public class ScheduleBatch
    {
        public ElementId StorageElementId { get; set; }
        public string Name { get; set; } = "";
        public List<ElementId> ScheduleIds { get; set; } = new List<ElementId>();
    }

    /// <summary>
    /// Reads and writes ScheduleBatch data to Extensible Storage on hidden
    /// DataStorage elements inside the project. Every method that writes must
    /// be called from inside an already open Transaction, this class does not
    /// open transactions itself so it can be reused from different commands.
    /// </summary>
    public static class BatchStorageService
    {
        public static List<ScheduleBatch> GetAllBatches(Document doc)
        {
            Schema schema = PinboardSchemaProvider.GetOrCreateSchema();
            List<ScheduleBatch> batches = new List<ScheduleBatch>();

            List<Element> dataStorageElements = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .ToList();

            foreach (Element element in dataStorageElements)
            {
                Entity entity = element.GetEntity(schema);
                if (!entity.IsValid())
                {
                    continue;
                }

                string name = entity.Get<string>(schema.GetField("BatchName"));
                IList<ElementId> ids = entity.Get<IList<ElementId>>(schema.GetField("ScheduleIds"));

                batches.Add(new ScheduleBatch
                {
                    StorageElementId = element.Id,
                    Name = name,
                    ScheduleIds = ids != null ? ids.ToList() : new List<ElementId>()
                });
            }

            return batches;
        }

        /// <summary>
        /// Saves a batch under the given name. If a batch with that name
        /// already exists its contents are overwritten, otherwise a new
        /// DataStorage element is created to hold it. Must be called inside
        /// an open Transaction.
        /// </summary>
        public static void SaveBatch(Document doc, string name, List<ElementId> scheduleIds)
        {
            Schema schema = PinboardSchemaProvider.GetOrCreateSchema();
            List<ScheduleBatch> existingBatches = GetAllBatches(doc);
            ScheduleBatch match = existingBatches.FirstOrDefault(b => b.Name == name);

            DataStorage storage;
            if (match != null)
            {
                storage = doc.GetElement(match.StorageElementId) as DataStorage;
            }
            else
            {
                storage = DataStorage.Create(doc);
            }

            Entity entity = new Entity(schema);
            entity.Set(schema.GetField("BatchName"), name);
            entity.Set<IList<ElementId>>(schema.GetField("ScheduleIds"), scheduleIds);

            storage.SetEntity(entity);
        }

        /// <summary>
        /// Deletes every saved batch matching that name, not just the first
        /// one found. Earlier versions of SaveBatch could occasionally
        /// leave a duplicate entry behind under the same name, this makes
        /// sure a delete actually clears all of them instead of leaving a
        /// duplicate that reappears the next time the list is opened.
        /// Must be called inside an open Transaction.
        /// </summary>
        public static void DeleteBatch(Document doc, string name)
        {
            List<ScheduleBatch> existingBatches = GetAllBatches(doc);
            List<ScheduleBatch> matches = existingBatches.Where(b => b.Name == name).ToList();
            foreach (ScheduleBatch match in matches)
            {
                doc.Delete(match.StorageElementId);
            }
        }
    }
}
