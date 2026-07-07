FROM mcr.microsoft.com/windows/nanoserver:ltsc2022

WORKDIR C:\agent

COPY bin\LocalFastAgent.exe C:\agent\LocalFastAgent.exe
COPY agentsettings.json C:\agent\agentsettings.json

ENTRYPOINT ["C:\\agent\\LocalFastAgent.exe"]
