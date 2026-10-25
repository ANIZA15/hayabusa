using System;

namespace Hayabusa.Rpc;

/// <summary>
/// Marks a partial class as a Hayabusa RPC or HTTP service for compile-time source generation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HayabusaServiceAttribute : Attribute
{
    /// <summary>Gets or sets an optional service route prefix.</summary>
    public string? Prefix { get; set; }
}

/// <summary>
/// Marks a method within a <see cref="HayabusaServiceAttribute"/> class as a binary MemoryPack RPC handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class RpcAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RpcAttribute"/> class.
    /// </summary>
    public RpcAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RpcAttribute"/> class with the specified method identifier.
    /// </summary>
    /// <param name="methodId">Unique positive integer identifier for this RPC method.</param>
    public RpcAttribute(int methodId)
    {
        MethodId = methodId;
    }

    /// <summary>Gets or sets the unique integer identifier for this RPC method.</summary>
    public int MethodId { get; set; }
}

/// <summary>
/// Marks a method within a <see cref="HayabusaServiceAttribute"/> class as an HTTP GET endpoint handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class GetAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GetAttribute"/> class with the specified route template.
    /// </summary>
    /// <param name="template">The HTTP route template (e.g., <c>"/health"</c> or <c>"/users/{id}"</c>).</param>
    public GetAttribute(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
    }

    /// <summary>Gets the route path template.</summary>
    public string Template { get; }
}

/// <summary>
/// Marks a method within a <see cref="HayabusaServiceAttribute"/> class as an HTTP POST endpoint handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PostAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PostAttribute"/> class with the specified route template.
    /// </summary>
    /// <param name="template">The HTTP route template (e.g., <c>"/rooms/join"</c>).</param>
    public PostAttribute(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
    }

    /// <summary>Gets the route path template.</summary>
    public string Template { get; }
}
