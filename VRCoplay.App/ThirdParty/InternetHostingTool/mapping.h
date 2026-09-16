#pragma once

struct IhtPort { int protocol; int port; int internalPort = 0; };
struct IhtRequest {
    const IhtPort* ports;
    int count;
    const char* owner;
    int lifetime;
};

// internalPort 0 uses port; upstream routers always forward port to port.
// Blocking, serialized. Reuse owner/ports to renew; lifetime 0 removes them.
// Returns 1 for a complete IPv4 chain, 0 or a negative UPnP error otherwise.
// address must hold 16 bytes.
extern "C" __declspec(dllexport) int IhtMap(const IhtRequest* request, char* address);

// Blocking, serialized, read-only discovery. Creates or removes no mappings.
extern "C" __declspec(dllexport) void IhtDiscover();
