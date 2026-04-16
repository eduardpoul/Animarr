# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install Node.js 22 LTS (apt ships Node 18 which is too old for @tailwindcss/oxide 4.x)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY ["src/Animarr.Web/Animarr.Web.csproj", "src/Animarr.Web/"]
RUN dotnet restore "src/Animarr.Web/Animarr.Web.csproj"

COPY . .
WORKDIR "/src/src/Animarr.Web"

# Install node deps after COPY so platform-specific optional packages
# (@tailwindcss/oxide-linux-x64-gnu) are resolved for the Linux container.
RUN npm install

RUN dotnet build "Animarr.Web.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Animarr.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Create data directory for SQLite
RUN mkdir -p /app/data && chmod 777 /app/data

COPY --from=publish /app/publish .

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Animarr.Web.dll"]
