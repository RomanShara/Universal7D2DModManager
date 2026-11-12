using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var path = args.Length > 0 ? args[0] : @"E:/7 Days To Die/7DaysToDie_Data/Managed/Assembly-CSharp.dll";
using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();

foreach (var handle in reader.TypeDefinitions)
{
    var type = reader.GetTypeDefinition(handle);
    var name = reader.GetString(type.Name);

    if (name is not ("Constants" or "VersionInformation")) continue;

    Console.WriteLine($"type: {name}");

    foreach (var fieldHandle in type.GetFields())
    {
        var field = reader.GetFieldDefinition(fieldHandle);
        var fieldName = reader.GetString(field.Name);

        if (field.GetDefaultValue() is not { IsNil: false } constantHandle) continue;

        var constant = reader.GetConstant(constantHandle);
        var blob = reader.GetBlobReader(constant.Value);
        var value = constant.TypeCode == ConstantTypeCode.String ? blob.ReadUTF16(blob.Length) : blob.ReadInt32().ToString();

        Console.WriteLine($"   {fieldName} = {value}");
    }
}
