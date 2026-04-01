FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore ArbiScan.slnx
RUN dotnet publish ArbiScan.Scanner/ArbiScan.Scanner.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production
ENV ArbiScan__Storage__RootPath=/app/storage

COPY --from=build /app/publish/ ./

RUN mkdir -p /app/storage/config /app/storage/logs /app/storage/data /app/storage/reports

ENTRYPOINT ["dotnet", "ArbiScan.Scanner.dll"]
