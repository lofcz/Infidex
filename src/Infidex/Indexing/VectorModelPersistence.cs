using Infidex.Api;
using Infidex.Core;

namespace Infidex.Indexing;

/// <summary>
/// Serialization logic for VectorModel index persistence.
/// </summary>
internal static class VectorModelPersistence
{
    private const string FormatVersion = "INFIDEX_V1";

    public static void SaveToStream(BinaryWriter writer, DocumentCollection documents, TermCollection termCollection)
    {
        writer.Write(FormatVersion);

        IReadOnlyList<Document> allDocs = documents.GetAllDocuments();
        writer.Write(allDocs.Count);
        foreach (Document doc in allDocs)
        {
            writer.Write(doc.Id);
            writer.Write(doc.DocumentKey);
            writer.Write(doc.IndexedText ?? string.Empty);
            writer.Write(doc.DocumentClientInformation ?? string.Empty);
            writer.Write(doc.SegmentNumber);
            writer.Write(doc.JsonIndex);
        }

        IEnumerable<Term> terms = termCollection.GetAllTerms();
        writer.Write(termCollection.Count);
        
        foreach (Term term in terms)
        {
            writer.Write(term.Text ?? string.Empty);
            writer.Write(term.DocumentFrequency);

            List<int>? docIds = term.GetDocumentIds();
            List<byte>? weights = term.GetWeights();

            int count = docIds?.Count ?? 0;
            writer.Write(count);

            if (count > 0 && docIds != null && weights != null)
            {
                for (int i = 0; i < count; i++)
                {
                    writer.Write(docIds[i]);
                    writer.Write(weights[i]);
                }
            }
        }
    }

    public static void LoadFromStream(BinaryReader reader, DocumentCollection documents, TermCollection termCollection, int stopTermLimit)
    {
        string version = reader.ReadString();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unknown index format: {version}");

        int docCount = reader.ReadInt32();
        for (int i = 0; i < docCount; i++)
        {
            int id = reader.ReadInt32();
            long key = reader.ReadInt64();
            string text = reader.ReadString();
            string info = reader.ReadString();
            int seg = reader.ReadInt32();
            int jsonIdx = reader.ReadInt32();

            DocumentFields fields = new DocumentFields();
            fields.AddField("content", text, Weight.Med, indexable: true);

            Document doc = new Document(key, seg, fields, info) { JsonIndex = jsonIdx, IndexedText = text };
            documents.AddDocument(doc);
        }

        int termCount = reader.ReadInt32();
        for (int i = 0; i < termCount; i++)
        {
            string text = reader.ReadString();
            int docFreq = reader.ReadInt32();
            int postingCount = reader.ReadInt32();

            Term term = termCollection.CountTermUsage(text, stopTermLimit, true);
            term.SetDocumentFrequency(docFreq);

            for (int j = 0; j < postingCount; j++)
            {
                int docId = reader.ReadInt32();
                byte weight = reader.ReadByte();
                term.AddForFastInsert(weight, docId);
            }
        }
    }
}

