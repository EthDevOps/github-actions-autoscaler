FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["GithubActionsOrchestrator.csproj", "./"]
RUN dotnet restore "GithubActionsOrchestrator.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "GithubActionsOrchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "GithubActionsOrchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Download Pyroscope native profiler
FROM base AS pyroscope-dl
USER root
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends wget && \
    mkdir -p /pyroscope && \
    PYRO_ARCH=$(if [ "$TARGETARCH" = "arm64" ]; then echo "aarch64"; else echo "x86_64"; fi) && \
    wget -qO- "https://github.com/grafana/pyroscope-dotnet/releases/download/v0.14.1-pyroscope/pyroscope.0.14.1-glibc-${PYRO_ARCH}.tar.gz" | \
    tar xz -C /pyroscope && \
    apt-get remove -y wget && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=pyroscope-dl /pyroscope /pyroscope

# Pyroscope CLR profiler
ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A}
ENV CORECLR_PROFILER_PATH=/pyroscope/Pyroscope.Profiler.Native.so
ENV LD_PRELOAD=/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so
ENV LD_LIBRARY_PATH=/pyroscope
ENV DOTNET_EnableDiagnostics=1
ENV DOTNET_EnableDiagnostics_IPC=0
ENV DOTNET_EnableDiagnostics_Debugger=0
ENV DOTNET_EnableDiagnostics_Profiler=1

ENTRYPOINT ["dotnet", "GithubActionsOrchestrator.dll"]
