using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Serialization;

/// <summary>
///     Reads and writes message bodies in the XML format produced by the NServiceBus <c>XmlSerializer</c>, for
///     interoperability with NServiceBus endpoints that still exchange XML on the wire. Register this as the
///     endpoint's default serializer after <c>UseNServiceBusInterop()</c> to send XML to NServiceBus. Every
///     NServiceBus interop endpoint already reads XML bodies by content type ("text/xml"), so receiving needs no
///     configuration.
/// </summary>
/// <remarks>
///     GH-5244. This is an independent implementation of the wire format, not a port of the NServiceBus
///     serializer: the root element is named after the message type in the
///     <c>http://tempuri.net/{type namespace}</c> namespace, each public property (then each public field) is a
///     child element, collection items are named after the item type (<c>String</c>, <c>Int32</c>, ...),
///     dictionary entries are <c>NServiceBus.KeyValuePairOf{Key}And{Value}</c> elements with <c>Key</c> and
///     <c>Value</c> children, a null <see cref="Nullable{T}" /> is written as <c>xsi:nil="true"</c> and a null
///     reference is left out. Reading matches elements by local name and ignores namespaces, so it accepts any
///     namespace NServiceBus was configured with.
/// </remarks>
public class NServiceBusXmlSerializer : IMessageSerializer
{
    /// <summary>
    ///     The content type NServiceBus stamps in the <c>NServiceBus.ContentType</c> header for XML bodies
    /// </summary>
    public const string XmlContentType = "text/xml";

    /// <summary>
    ///     The base namespace NServiceBus uses by default. The message type's namespace is appended to it.
    /// </summary>
    public const string DefaultNamespace = "http://tempuri.net";

    private const string AotJustification =
        "Reflection-based interop serializer, like the default JSON serializers; AOT apps using NServiceBus XML interop preserve their message types via TrimmerRootDescriptor.";

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    // NServiceBus writes the declaration without an encoding attribute and the body without a BOM
    private static readonly byte[] _declaration = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>");

    private static readonly XmlWriterSettings _writerSettings = new()
    {
        OmitXmlDeclaration = true,
        Encoding = new UTF8Encoding(false),
        Indent = false
    };

    private ImHashMap<Type, XmlMember[]> _members = ImHashMap<Type, XmlMember[]>.Empty;

    /// <summary>
    ///     The base namespace for root elements. Matches NServiceBus' <c>XmlSerializer.Namespace(...)</c>
    ///     setting, and defaults to <see cref="DefaultNamespace" />
    /// </summary>
    public string Namespace { get; set; } = DefaultNamespace;

    public string ContentType => XmlContentType;

    public byte[] Write(Envelope envelope)
    {
        return WriteMessage(envelope.Message!);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return read(messageType, envelope.Data!);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException(
            $"{nameof(NServiceBusXmlSerializer)} needs to know the message type to read a body. NServiceBus names it in the 'NServiceBus.EnclosedMessageTypes' header.");
    }

    public byte[] WriteMessage(object message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var messageType = message.GetType();
        using var stream = new MemoryStream();
        stream.Write(_declaration);

        using (var writer = XmlWriter.Create(stream, _writerSettings))
        {
            var ns = messageType.Namespace.IsEmpty() ? Namespace : $"{Namespace}/{messageType.Namespace}";
            writer.WriteStartElement(messageType.Name, ns);
            writer.WriteAttributeString("xmlns", "xsi", null, XsiNamespace);
            writer.WriteAttributeString("xmlns", "xsd", null, XsdNamespace);
            writeMembers(writer, message, ns);
            writer.WriteFullEndElement();
        }

        return stream.ToArray();
    }

    private object read(Type messageType, byte[] data)
    {
        if (messageType.IsInterface || messageType.IsAbstract)
        {
            throw new InvalidOperationException(
                $"Cannot read an NServiceBus XML message into {messageType.FullNameInCode()} because it is not a concrete type. Use opts.Policies.RegisterInteropMessageAssembly() so Wolverine can map NServiceBus interface messages to concrete types.");
        }

        XElement root;
        using (var stream = new MemoryStream(data))
        {
            root = XDocument.Load(stream).Root
                   ?? throw new InvalidOperationException("The NServiceBus XML message body has no root element");
        }

        // Older NServiceBus versions batch messages inside a <Messages> wrapper
        if (root.Name.LocalName == "Messages" && messageType.Name != "Messages" && root.Elements().FirstOrDefault() is { } first)
        {
            root = first;
        }

        return readObject(messageType, root);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = AotJustification)]
    private XmlMember[] membersFor(Type type)
    {
        if (_members.TryFind(type, out var members))
        {
            return members;
        }

        // NServiceBus writes public properties before public fields, and a derived type's own members first
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0)
            .Select(x => new XmlMember(x.Name, x.PropertyType, x.GetValue, x.CanWrite ? x.SetValue : null));

        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => !x.IsInitOnly)
            .Select(x => new XmlMember(x.Name, x.FieldType, x.GetValue, x.SetValue));

        members = properties.Concat(fields).ToArray();
        _members = _members.AddOrUpdate(type, members);

        return members;
    }

    private void writeMembers(XmlWriter writer, object target, string ns)
    {
        foreach (var member in membersFor(target.GetType()))
        {
            writeValue(writer, member.Name, member.Type, member.Getter(target), ns);
        }
    }

    private void writeValue(XmlWriter writer, string elementName, Type declaredType, object? value, string ns)
    {
        if (value == null)
        {
            if (Nullable.GetUnderlyingType(declaredType) != null)
            {
                writer.WriteStartElement(elementName, ns);
                writer.WriteAttributeString("nil", XsiNamespace, "true");
                writer.WriteFullEndElement();
            }

            // NServiceBus leaves null references out altogether
            return;
        }

        writer.WriteStartElement(elementName, ns);

        if (tryFormatScalar(value, out var text))
        {
            writer.WriteString(text);
        }
        else if (value is IDictionary dictionary)
        {
            var (keyType, valueType) = dictionaryTypes(declaredType, value.GetType());
            var entryName = $"NServiceBus.KeyValuePairOf{keyType.Name}And{valueType.Name}";
            foreach (DictionaryEntry entry in dictionary)
            {
                writer.WriteStartElement(entryName, ns);
                writeValue(writer, "Key", keyType, entry.Key, ns);
                writeValue(writer, "Value", valueType, entry.Value, ns);
                writer.WriteFullEndElement();
            }
        }
        else if (value is IEnumerable enumerable)
        {
            var itemType = elementType(declaredType) ?? elementType(value.GetType()) ?? typeof(object);
            foreach (var item in enumerable)
            {
                var itemElementType = item?.GetType() ?? itemType;
                writeValue(writer, (Nullable.GetUnderlyingType(itemElementType) ?? itemElementType).Name, itemType,
                    item, ns);
            }
        }
        else
        {
            writeMembers(writer, value, ns);
        }

        writer.WriteFullEndElement();
    }

    private static bool tryFormatScalar(object value, [NotNullWhen(true)] out string? text)
    {
        text = value switch
        {
            string s => s,
            bool b => XmlConvert.ToString(b),
            Guid g => g.ToString(),
            DateTime d => XmlConvert.ToString(d, XmlDateTimeSerializationMode.RoundtripKind),
            DateTimeOffset d => XmlConvert.ToString(d),
            TimeSpan t => XmlConvert.ToString(t),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            char c => c.ToString(),
            Uri uri => uri.ToString(),
            float f => XmlConvert.ToString(f),
            double d => XmlConvert.ToString(d),
            decimal d => XmlConvert.ToString(d),
            IFormattable formattable when value.GetType().IsPrimitive =>
                formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => null
        };

        return text != null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = AotJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = AotJustification)]
    private object? readValue(Type type, XElement element)
    {
        if (element.Attribute(XName.Get("nil", XsiNamespace)) is { } nil && XmlConvert.ToBoolean(nil.Value))
        {
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (tryParseScalar(underlying, element.Value, out var scalar))
        {
            return scalar;
        }

        if (underlying.IsArray)
        {
            var itemType = underlying.GetElementType()!;
            var items = element.Elements().Select(x => readValue(itemType, x)).ToArray();
            var array = Array.CreateInstance(itemType, items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                array.SetValue(items[i], i);
            }

            return array;
        }

        if (dictionaryTypesOrNull(underlying) is var (keyType, valueType))
        {
            var dictionary = (IDictionary)createCollection(underlying, typeof(Dictionary<,>).MakeGenericType(keyType, valueType));
            foreach (var entry in element.Elements())
            {
                var key = childOf(entry, "Key") is { } keyElement ? readValue(keyType, keyElement) : null;
                if (key == null) continue;

                dictionary[key] = childOf(entry, "Value") is { } valueElement ? readValue(valueType, valueElement) : null;
            }

            return dictionary;
        }

        if (underlying != typeof(string) && elementType(underlying) is { } collectionItemType)
        {
            var collection = createCollection(underlying, typeof(List<>).MakeGenericType(collectionItemType));
            var add = collection.GetType().GetMethod("Add", [collectionItemType])
                      ?? throw new NotSupportedException(
                          $"{nameof(NServiceBusXmlSerializer)} cannot add items to a {underlying.FullNameInCode()}");

            foreach (var child in element.Elements())
            {
                add.Invoke(collection, [readValue(collectionItemType, child)]);
            }

            return collection;
        }

        return readObject(underlying, element);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = AotJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = AotJustification)]
    private object readObject(Type type, XElement element)
    {
        var target = type.GetConstructor(Type.EmptyTypes) != null
            ? Activator.CreateInstance(type)!
            : RuntimeHelpers.GetUninitializedObject(type);

        var members = membersFor(type);
        foreach (var child in element.Elements())
        {
            var member = members.FirstOrDefault(x => x.Name == child.Name.LocalName);
            if (member?.Setter == null) continue;

            member.Setter(target, readValue(member.Type, child));
        }

        return target;
    }

    private static XElement? childOf(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
    }

    private static bool tryParseScalar(Type type, string text, out object? value)
    {
        value = null;

        if (type == typeof(string)) value = text;
        else if (type.IsEnum) value = Enum.Parse(type, text);
        else if (type == typeof(Guid)) value = Guid.Parse(text);
        else if (type == typeof(bool)) value = XmlConvert.ToBoolean(text);
        else if (type == typeof(DateTime)) value = XmlConvert.ToDateTime(text, XmlDateTimeSerializationMode.RoundtripKind);
        else if (type == typeof(DateTimeOffset)) value = XmlConvert.ToDateTimeOffset(text);
        else if (type == typeof(TimeSpan)) value = XmlConvert.ToTimeSpan(text);
        else if (type == typeof(DateOnly)) value = DateOnly.Parse(text, CultureInfo.InvariantCulture);
        else if (type == typeof(TimeOnly)) value = TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        else if (type == typeof(byte[])) value = Convert.FromBase64String(text);
        else if (type == typeof(char)) value = text.Length == 0 ? default : text[0];
        else if (type == typeof(Uri)) value = new Uri(text, UriKind.RelativeOrAbsolute);
        else if (type == typeof(float)) value = XmlConvert.ToSingle(text);
        else if (type == typeof(double)) value = XmlConvert.ToDouble(text);
        else if (type == typeof(decimal)) value = XmlConvert.ToDecimal(text);
        else if (type.IsPrimitive) value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        else return false;

        return true;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = AotJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = AotJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = AotJustification)]
    private static object createCollection(Type declaredType, Type fallbackType)
    {
        // A concrete collection type is built as itself; an interface like IList<T> or IReadOnlyDictionary<K,V>
        // gets the List<T> or Dictionary<K,V> that satisfies it
        return !declaredType.IsInterface && !declaredType.IsAbstract && declaredType.GetConstructor(Type.EmptyTypes) != null
            ? Activator.CreateInstance(declaredType)!
            : Activator.CreateInstance(fallbackType)!;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = AotJustification)]
    private static Type? elementType(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[])) return null;
        if (type.IsArray) return type.GetElementType();

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerable?.GetGenericArguments()[0];
    }

    private static (Type Key, Type Value) dictionaryTypes(Type declaredType, Type actualType)
    {
        return dictionaryTypesOrNull(declaredType) ?? dictionaryTypesOrNull(actualType) ?? (typeof(object), typeof(object));
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = AotJustification)]
    private static (Type Key, Type Value)? dictionaryTypesOrNull(Type type)
    {
        bool isDictionary(Type t) => t.IsGenericType &&
                                     (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                      t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        var dictionary = isDictionary(type) ? type : type.GetInterfaces().FirstOrDefault(isDictionary);
        if (dictionary == null) return null;

        var arguments = dictionary.GetGenericArguments();
        return (arguments[0], arguments[1]);
    }

    private record XmlMember(string Name, Type Type, Func<object, object?> Getter, Action<object, object?>? Setter);
}
