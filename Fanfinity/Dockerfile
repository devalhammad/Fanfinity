# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Fanfinity.csproj", "./"]
RUN dotnet restore "Fanfinity.csproj"

COPY . .
RUN dotnet build "Fanfinity.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Fanfinity.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Fanfinity.dll"]