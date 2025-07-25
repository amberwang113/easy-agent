@echo Off
set _config=%1
if "%_config%" == "" (
   set _config=Release
)
dotnet restore
dotnet build --configuration %_config%
dotnet publish --configuration %_config%
nuget pack EasyAgent.nuspec -OutputDirectory bin\Release
