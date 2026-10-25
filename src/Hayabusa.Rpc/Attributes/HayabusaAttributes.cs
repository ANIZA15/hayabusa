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

/// <summary>
/// Marks an interface as a server-to-client real-time push receiver contract (MagicOnion <c>IStreamingHub</c> receiver style)
/// for compile-time generation of zero-allocation room broadcasters and client receiver binders.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class HayabusaHubReceiverAttribute : Attribute
{
}

/// <summary>
/// MagicOnion-compatible marker interface for strongly-typed unary RPC service contracts shared between client and server.
/// </summary>
/// <typeparam name="TSelf">The service interface type itself.</typeparam>
#pragma warning disable CA1040 // Marker interface deliberately mirrors MagicOnion IService<TSelf> contract
public interface IService<TSelf>
    where TSelf : IService<TSelf>
{
}

/// <summary>
/// MagicOnion-compatible marker interface for real-time StreamingHub contracts shared between client and server,
/// pairing a server hub interface (<typeparamref name="TSelf"/>) with a server-to-client receiver interface (<typeparamref name="TReceiver"/>).
/// </summary>
/// <typeparam name="TSelf">The hub interface type itself.</typeparam>
/// <typeparam name="TReceiver">The client receiver interface type for server-to-client push notifications.</typeparam>
public interface IStreamingHub<TSelf, TReceiver> : IService<TSelf>
    where TSelf : IStreamingHub<TSelf, TReceiver>
{
}
#pragma warning restore CA1040

