
private struct __DotNet__AttributeStruct
{
    enum Target {
        none,
        assembly,
        module_,
    }
    Target target;
    string stringOfType;
    this(Target target, string stringOfType)
    {
        this.target = target;
        this.stringOfType = stringOfType;
    }
    this(string stringOfType)
    {
        this.target = Target.none;
        this.stringOfType = stringOfType;
    }
}
template __DotNet__Attribute(T...)
{
    static if(is(typeof(T[0]) == string))
    {
        immutable __DotNet__AttributeStruct __DotNet__Attribute =
            __DotNet__AttributeStruct(T[0]);
    }
    else
    {
        immutable __DotNet__AttributeStruct __DotNet__Attribute =
            __DotNet__AttributeStruct(T[0], T[1]);
    }
}
