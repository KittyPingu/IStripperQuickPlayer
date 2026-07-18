using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections;
using System.Formats.Nrbf;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace IStripperQuickPlayer.DataModel
{
    internal static class Persistence
    {
        private const long MaxLegacyBytes = 64 * 1024 * 1024;
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver
            {
                IgnoreSerializableAttribute = false
            },
            TypeNameHandling = TypeNameHandling.None
        };

        internal static bool IsLegacy(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return NrbfDecoder.StartsWithPayloadHeader(stream);
        }

        internal static T Load<T>(string path)
        {
            using FileStream stream = File.OpenRead(path);
            if (NrbfDecoder.StartsWithPayloadHeader(stream))
            {
                if (stream.Length > MaxLegacyBytes)
                    throw new InvalidDataException("The legacy data file is too large to migrate safely.");

                T migrated = LegacyNrbf.Read<T>(stream);
                string backup = GetBackupPath(path);
                File.Copy(path, backup);
                Save(path, migrated);
                return migrated;
            }

            using StreamReader text = new(stream, Encoding.UTF8);
            using JsonTextReader json = new(text);
            return JsonSerializer.Create(JsonSettings).Deserialize<T>(json)
                ?? throw new InvalidDataException("The data file was empty.");
        }

        internal static void Save<T>(string path, T value)
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            string temporaryPath = path + ".tmp";
            try
            {
                using (FileStream stream = File.Create(temporaryPath))
                using (StreamWriter text = new(stream, new UTF8Encoding(false)))
                using (JsonTextWriter json = new(text))
                    JsonSerializer.Create(JsonSettings).Serialize(json, value);

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        internal static string MoveLegacyAside(string path)
        {
            string backup = GetBackupPath(path);
            File.Move(path, backup);
            return backup;
        }

        internal static bool VerifyMigration(string folder)
        {
            string filtersPath = Path.Combine(folder, "filters.bin");
            string myDataPath = Path.Combine(folder, "mydata.bin");
            Dictionary<string, FilterSettings> filters =
                Load<Dictionary<string, FilterSettings>>(filtersPath);
            MyData myData = Load<MyData>(myDataPath);
            string expectedFilters = JsonConvert.SerializeObject(filters, JsonSettings);
            string expectedMyData = JsonConvert.SerializeObject(myData, JsonSettings);

            return !IsLegacy(filtersPath) &&
                !IsLegacy(myDataPath) &&
                expectedFilters == JsonConvert.SerializeObject(
                    Load<Dictionary<string, FilterSettings>>(filtersPath), JsonSettings) &&
                expectedMyData == JsonConvert.SerializeObject(Load<MyData>(myDataPath), JsonSettings);
        }

        private static string GetBackupPath(string path)
        {
            string backup = path + ".binary-backup";
            return File.Exists(backup)
                ? backup + "." + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)
                : backup;
        }

        private static class LegacyNrbf
        {
            private const int MaxDepth = 32;
            private const int MaxArrayItems = 1_000_000;

            internal static T Read<T>(Stream stream)
            {
                object? value = ConvertValue(NrbfDecoder.Decode(stream), typeof(T), 0);
                return value is T result
                    ? result
                    : throw new InvalidDataException($"Legacy data did not contain {typeof(T).Name}.");
            }

            private static object? ConvertValue(object? value, Type expectedType, int depth)
            {
                if (depth > MaxDepth)
                    throw new InvalidDataException("The legacy data is nested too deeply.");
                if (value is null)
                    return null;

                Type targetType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;
                if (targetType.IsInstanceOfType(value))
                    return value;

                if (value is SerializationRecord primitiveRecord &&
                    value is not ClassRecord &&
                    value is not ArrayRecord)
                {
                    value = primitiveRecord.GetType().GetProperty("Value")?.GetValue(primitiveRecord);
                }

                if (targetType.IsEnum)
                    return Enum.ToObject(targetType, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                if (targetType == typeof(string) || targetType.IsPrimitive ||
                    targetType == typeof(decimal) || targetType == typeof(DateTime))
                {
                    return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }

                if (targetType.IsArray && value is ArrayRecord arrayRecord)
                {
                    Type elementType = targetType.GetElementType()
                        ?? throw new InvalidDataException("Legacy array type was invalid.");
                    object?[] values = GetArrayValues(arrayRecord);
                    Array result = Array.CreateInstance(elementType, values.Length);
                    for (int index = 0; index < values.Length; index++)
                        result.SetValue(ConvertValue(values[index], elementType, depth + 1), index);
                    return result;
                }

                if (targetType.IsGenericType &&
                    targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    return ReadDictionary(RequireClass(value, "dictionary"), targetType, depth + 1);
                }

                if (targetType.IsGenericType &&
                    targetType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return ReadList(RequireClass(value, "list"), targetType, depth + 1);
                }

                ClassRecord record = RequireClass(value, targetType.Name);
                if (!string.Equals(record.TypeName.FullName, targetType.FullName, StringComparison.Ordinal))
                    throw new InvalidDataException($"Unexpected legacy type {record.TypeName.FullName}.");

                object instance = Activator.CreateInstance(targetType, nonPublic: true)
                    ?? throw new InvalidDataException($"Could not create {targetType.Name}.");
                foreach (FieldInfo field in targetType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.GetCustomAttribute<NonSerializedAttribute>() is null &&
                        record.HasMember(field.Name))
                    {
                        field.SetValue(instance,
                            ConvertValue(record.GetRawValue(field.Name), field.FieldType, depth + 1));
                    }
                }
                return instance;
            }

            private static object ReadDictionary(ClassRecord record, Type dictionaryType, int depth)
            {
                if (!record.TypeName.FullName.StartsWith(
                    "System.Collections.Generic.Dictionary`2", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Legacy data contained an unexpected dictionary type.");
                }

                IDictionary dictionary = (IDictionary)(Activator.CreateInstance(dictionaryType)
                    ?? throw new InvalidDataException("Could not create a dictionary."));
                if (!record.HasMember("KeyValuePairs"))
                    return dictionary;

                Type[] arguments = dictionaryType.GetGenericArguments();
                foreach (object? item in GetArrayValues(record.GetArrayRecord("KeyValuePairs")))
                {
                    ClassRecord pair = RequireClass(item, "dictionary entry");
                    object? key = ConvertValue(pair.GetRawValue("key"), arguments[0], depth + 1);
                    object? entryValue = ConvertValue(pair.GetRawValue("value"), arguments[1], depth + 1);
                    if (key is null)
                        throw new InvalidDataException("Legacy dictionary contained a null key.");
                    dictionary.Add(key, entryValue);
                }
                return dictionary;
            }

            private static object ReadList(ClassRecord record, Type listType, int depth)
            {
                if (!record.TypeName.FullName.StartsWith(
                    "System.Collections.Generic.List`1", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Legacy data contained an unexpected list type.");
                }

                IList list = (IList)(Activator.CreateInstance(listType)
                    ?? throw new InvalidDataException("Could not create a list."));
                int size = record.GetInt32("_size");
                object?[] values = GetArrayValues(record.GetArrayRecord("_items"));
                if (size < 0 || size > values.Length)
                    throw new InvalidDataException("Legacy list size was invalid.");

                Type elementType = listType.GetGenericArguments()[0];
                for (int index = 0; index < size; index++)
                    list.Add(ConvertValue(values[index], elementType, depth + 1));
                return list;
            }

            private static object?[] GetArrayValues(ArrayRecord record)
            {
                if (record.Rank != 1 || record.Lengths[0] > MaxArrayItems)
                    throw new InvalidDataException("Legacy array dimensions were invalid.");

                MethodInfo? getArray = record.GetType().GetMethod(
                    "GetArray",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(bool) },
                    modifiers: null);
                if (getArray?.Invoke(record, new object[] { true }) is not IEnumerable values)
                    throw new InvalidDataException("Legacy array could not be read.");

                return values.Cast<object?>().ToArray();
            }

            private static ClassRecord RequireClass(object? value, string description)
            {
                return value as ClassRecord
                    ?? throw new InvalidDataException($"Legacy {description} was invalid.");
            }
        }
    }
}
