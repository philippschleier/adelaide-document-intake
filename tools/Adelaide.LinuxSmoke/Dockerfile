# Runs the native-load smoke in the EXACT runtime image we ship (mcr.microsoft.com/dotnet/aspnet:10.0) — a slim image
# that can lack system libs a full Ubuntu happens to have. A pass here proves the natives load on the Linux we DEPLOY,
# not merely on a full distro; this also doubles as the Dockerfile's own `docker build && docker run` validation.
#
# DELIBERATELY no `apt-get install libfontconfig1`. PDFtoImage pulls SkiaSharp's `.NoDependencies` variant, built to NOT
# require libfontconfig — but that is a claim until proven. This image IS the experiment:
#   • smoke passes here  → .NoDependencies is genuinely fontconfig-free; keep the image lean (no install needed).
#   • smoke FAILS on a font path → libfontconfig1 is load-bearing; add it (and keep it):
#       RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 && rm -rf /var/lib/apt/lists/*
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /smoke
COPY publish/ ./
ENTRYPOINT ["dotnet", "Adelaide.LinuxSmoke.dll"]
