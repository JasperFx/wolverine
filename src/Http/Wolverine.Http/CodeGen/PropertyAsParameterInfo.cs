using System.Reflection;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// Presents a flattened [FromQuery] property as a parameter, so OpenAPI generators that read annotations from a
/// <see cref="ParameterInfo"/> see the property's. ASP.NET Core describes [AsParameters] members the same way.
/// </summary>
internal sealed class PropertyAsParameterInfo : ParameterInfo
{
    private readonly PropertyInfo _property;

    public PropertyAsParameterInfo(PropertyInfo property)
    {
        _property = property;
        NameImpl = property.Name;
        MemberImpl = property;
        ClassImpl = property.PropertyType;
        PositionImpl = -1;
    }

    public override bool HasDefaultValue => false;

    public override int MetadataToken => _property.MetadataToken;

    public override object[] GetCustomAttributes(bool inherit) => _property.GetCustomAttributes(inherit);

    public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
        _property.GetCustomAttributes(attributeType, inherit);

    public override IList<CustomAttributeData> GetCustomAttributesData() => _property.GetCustomAttributesData();

    public override bool IsDefined(Type attributeType, bool inherit) => _property.IsDefined(attributeType, inherit);

    public override Type[] GetOptionalCustomModifiers() => _property.GetOptionalCustomModifiers();

    public override Type[] GetRequiredCustomModifiers() => _property.GetRequiredCustomModifiers();
}
