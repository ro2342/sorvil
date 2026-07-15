using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sorvil.Models;
using Windows.Data.Json;
using Windows.Storage;

namespace Sorvil.Services
{
    // Índice local de livros baixados + posição de leitura, em JSON dentro
    // de ApplicationData.Current.LocalFolder — mesmo desenho de
    // LocalDataStore.cs do theartistsway (um arquivo por "store").
    public static class LibraryDataStore
    {
        private const string FileName = "books.json";

        private static async Task<JsonObject> ReadAllAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new JsonObject();
                }
                return JsonObject.Parse(text);
            }
            catch (FileNotFoundException)
            {
                return new JsonObject();
            }
        }

        private static async Task WriteAllAsync(JsonObject all)
        {
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, all.Stringify());
        }

        public static async Task<List<BookRecord>> GetAllAsync()
        {
            JsonObject all = await ReadAllAsync();
            List<BookRecord> records = new List<BookRecord>();
            foreach (string key in all.Keys)
            {
                records.Add(ToRecord(key, all[key].GetObject()));
            }
            return records;
        }

        public static async Task<BookRecord> GetAsync(string id)
        {
            JsonObject all = await ReadAllAsync();
            if (!all.ContainsKey(id))
            {
                return null;
            }
            return ToRecord(id, all[id].GetObject());
        }

        public static async Task SaveAsync(BookRecord record)
        {
            JsonObject all = await ReadAllAsync();
            all[record.Id] = ToJson(record);
            await WriteAllAsync(all);
        }

        public static async Task DeleteAsync(string id)
        {
            JsonObject all = await ReadAllAsync();
            if (all.ContainsKey(id))
            {
                all.Remove(id);
                await WriteAllAsync(all);
            }
        }

        public static async Task TouchLastOpenedAsync(string id)
        {
            BookRecord record = await GetAsync(id);
            if (record == null)
            {
                return;
            }
            record.LastOpenedAt = DateTimeOffset.UtcNow.ToString("o");
            await SaveAsync(record);
        }

        private static IJsonValue ToJson(BookRecord record)
        {
            JsonObject obj = new JsonObject();
            obj["title"] = JsonValue.CreateStringValue(record.Title ?? string.Empty);
            obj["author"] = JsonValue.CreateStringValue(record.Author ?? string.Empty);
            obj["format"] = JsonValue.CreateStringValue(record.Format ?? string.Empty);
            obj["localFilePath"] = JsonValue.CreateStringValue(record.LocalFilePath ?? string.Empty);
            obj["coverCacheKey"] = JsonValue.CreateStringValue(record.CoverCacheKey ?? string.Empty);
            obj["lastOpenedAt"] = JsonValue.CreateStringValue(record.LastOpenedAt ?? string.Empty);
            obj["readingPositionJson"] = JsonValue.CreateStringValue(record.ReadingPositionJson ?? string.Empty);
            return obj;
        }

        private static BookRecord ToRecord(string id, JsonObject obj)
        {
            return new BookRecord
            {
                Id = id,
                Title = GetString(obj, "title"),
                Author = GetString(obj, "author"),
                Format = GetString(obj, "format"),
                LocalFilePath = GetString(obj, "localFilePath"),
                CoverCacheKey = GetString(obj, "coverCacheKey"),
                LastOpenedAt = GetString(obj, "lastOpenedAt"),
                ReadingPositionJson = GetString(obj, "readingPositionJson"),
            };
        }

        private static string GetString(JsonObject obj, string key)
        {
            return obj.ContainsKey(key) ? obj[key].GetString() : null;
        }
    }
}
