# Install all required libs
FROM matthid/fsharp-docker

RUN mkdir /clustercfg && mkdir /workingDir && mkdir /build

# for clustermanagement
ENV CM_STORAGE=/clustercfg/.cm
ENV CM_TEMP_STORAGE=/clustercfg/.cm-temp
VOLUME [ "/clustercfg" ]


COPY . /build

# Build and cleanup
RUN cd /build && /bin/bash build.sh && cp -a /build/ClusterManagement/bin/Release /app && cd /app && rm -rf /build && rm -rf /root/.nuget && rm -rf /root/.local/share/NuGet/Cache && rm -rf /tmp/* && rm -rf /root/.cache

WORKDIR /workingDir
ENTRYPOINT [ "mono", "--debug", "/app/ClusterManagement.exe" ]

# docker build --squash . -t matthid/clustermanagement:latest -t matthid/clustermanagement:0.1.0 && docker push matthid/clustermanagement:0.1.0 && docker push matthid/clustermanagement:latest
# MSYS_NO_PATHCONV=1 docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v $PROJDIR/clustercgf:/clustercfg -v $PROJDIR:/workingdir matthid/clustermanagement
