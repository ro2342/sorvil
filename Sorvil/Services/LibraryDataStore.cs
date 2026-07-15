using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

        // Sem isso, duas chamadas concorrentes (ex.: o leitor salvando a
        // posição a cada toque de página enquanto o usuário também arrasta
        // um slider de fonte, que também salva a cada tique) abrem o MESMO
        // arquivo ao mesmo tempo — StorageFile não serializa isso sozinho,
        // e o resultado real no aparelho era "the process cannot access
        // the file because it is being used by another process" toda vez
        // que qualquer coisa no leitor era tocada. Um semáforo local
        // garante que só uma leitura+escrita mexe no arquivo por vez.
        private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);

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
            await FileLock.WaitAsync();
            try
            {
                JsonObject all = await ReadAllAsync();
                List<BookRecord> records = new List<BookRecord>();
                foreach (string key in all.Keys)
                {
                    records.Add(ToRecord(key, all[key].GetObject()));
                }
                return records;
            }
            finally
            {
                FileLock.Release();
            }
        }

        public static async Task<BookRecord> GetAsync(string id)
        {
            await FileLock.WaitAsync();
            try
            {
                return await GetCoreAsync(id);
            }
            finally
            {
                FileLock.Release();
            }
        }

        public static async Task SaveAsync(BookRecord record)
        {
            await FileLock.WaitAsync();
            try
            {
                await SaveCoreAsync(record);
            }
            finally
            {
                FileLock.Release();
            }
        }

        public static async Task DeleteAsync(string id)
        {
            await FileLock.WaitAsync();
            try
            {
                JsonObject all = await ReadAllAsync();
                if (all.ContainsKey(id))
                {
                    all.Remove(id);
                    await WriteAllAsync(all);
                }
            }
            finally
            {
                FileLock.Release();
            }
        }

        // Lê e grava dentro da MESMA posse do semáforo — se cada metade
        // tomasse o lock por conta própria, essa chamada composta ia
        // travar esperando a si mesma (SemaphoreSlim não é reentrante).
        public static async Task TouchLastOpenedAsync(string id)
        {
            await FileLock.WaitAsync();
            try
            {
                BookRecord record = await GetCoreAsync(id);
                if (record == null)
                {
                    return;
                }
                record.LastOpenedAt = DateTimeOffset.UtcNow.ToString("o");
                await SaveCoreAsync(record);
            }
            finally
            {
                FileLock.Release();
            }
        }

        private static async Task<BookRecord> GetCoreAsync(string id)
        {
            JsonObject all = await ReadAllAsync();
            if (!all.ContainsKey(id))
            {
                return null;
            }
            return ToRecord(id, all[id].GetObject());
        }

        private static async Task SaveCoreAsync(BookRecord record)
        {
            JsonObject all = await ReadAllAsync();
            all[record.Id] = ToJson(record);
            await WriteAllAsync(all);
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
