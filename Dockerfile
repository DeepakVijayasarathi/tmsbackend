FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Tms.Api/Tms.Api.csproj src/Tms.Api/
RUN dotnet restore src/Tms.Api/Tms.Api.csproj
COPY src/ src/
RUN dotnet publish src/Tms.Api/Tms.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Tms.Api.dll"]
