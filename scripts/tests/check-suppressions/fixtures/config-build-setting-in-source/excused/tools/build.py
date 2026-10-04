import subprocess

subprocess.run(["dotnet", "build", "-nowarn:CS8618"])  # Agreed with the team: the legacy module is too noisy to fix this quarter.
