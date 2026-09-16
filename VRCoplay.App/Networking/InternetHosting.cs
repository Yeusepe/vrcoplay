// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
namespace VRCoplay;
internal static class InternetHosting
{
    internal readonly record struct Result(IPAddress? Address, int ErrorCode = 0)
    {
        internal string? Error =>
            ErrorCode switch
            {
                0 => null,
                728 => "The router has no port mappings available.",
                501 => "The router could not create or renew the port mapping (UPnP 501).",
                718 or 729 => $"The router already has a different mapping for a requested port (UPnP {ErrorCode}).",
                _ => $"The router rejected port forwarding (UPnP {ErrorCode}).",
            };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Port(int Protocol, int PublicPort, int PrivatePort = 0);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private readonly record struct Request(nint Ports, int Count, string Owner, int Lifetime);
    [DllImport(
        "ThirdParty/InternetHostingTool/iht.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int IhtMap(ref Request request, [Out] StringBuilder address);
    [DllImport("ThirdParty/InternetHostingTool/iht.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern void IhtDiscover();
    private static readonly Lazy<Task> Discovery = new(async () =>
    {
        try
        {
            await Task.Run(IhtDiscover).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Router discovery: {error.Message}");
        }
    });
    internal static Task DiscoverAsync() => Discovery.Value;
    internal static unsafe Task<Result> UpdateAsync(Port[] ports, string owner, int lifetime) =>
        Task.Run(() =>
        {
            fixed (Port* pinned = ports)
            {
                var request = new Request((nint)pinned, ports.Length, owner, lifetime);
                var address = new StringBuilder(16);
                var result = IhtMap(ref request, address);
                return new Result(
                    result == 1 && IPAddress.TryParse(address.ToString(), out var ip) ? ip : null,
                    result < 0 ? -result : 0
                );
            }
        });
}
