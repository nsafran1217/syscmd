# syscmd in a container.
#
#   docker build -t syscmd .
#   docker run --rm -p 5080:5080 syscmd --simulate          # the fake lab, no hardware needed
#   docker run -d -p 5080:5080 \
#     -v /srv/syscmd/config:/app/config \
#     -v /srv/syscmd/data:/app/data syscmd                  # real hardware
#
# Anything after the image name is passed to the app, which is how --simulate gets in.

# --------------------------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so editing source does not re-download packages.
# The server references the other two, so restoring it restores all three.
COPY src/SysCmd.Core/SysCmd.Core.csproj           src/SysCmd.Core/
COPY src/SysCmd.Server/SysCmd.Server.csproj       src/SysCmd.Server/
COPY src/SysCmd.Simulator/SysCmd.Simulator.csproj src/SysCmd.Simulator/
RUN dotnet restore src/SysCmd.Server/SysCmd.Server.csproj

COPY src/ src/
RUN dotnet publish src/SysCmd.Server/SysCmd.Server.csproj -c Release -o /app --no-restore

# ------------------------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app ./

# The simulated lab ships with the image, so `docker run <image> --simulate` is a working
# demonstration with no volumes, no hardware and nothing to configure first.
COPY config.sim/ ./config.sim/

# syscmd locates its configuration by walking up from the content root for a directory that
# holds config.sim, so shipping config.sim here makes /app the repo root as far as the app is
# concerned: /app/config for real hardware, /app/config.sim for the fake lab, with data
# alongside each. Both data directories are created up front because the app writes its event
# log and power history into them and does not run as root.
#
# config is writable on purpose: the configuration pages save YAML back through ConfigStore.
# app is the non-root user the .NET base images create, uid and gid both 1654 (runtime-deps sets
# APP_UID and useradds against it). Named rather than numbered here so this keeps working if that
# number ever moves; the README says how to check it.
RUN mkdir -p config data data.sim && chown -R app:app config data data.sim

ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080

# There is no authentication - see the security posture in README.md. Publish this port only on
# a network you trust, because anything that can reach it can power machines off.
USER app

ENTRYPOINT ["dotnet", "SysCmd.Server.dll"]
