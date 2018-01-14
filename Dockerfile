# Install all required libs
FROM matthid/fsharp-docker

RUN mkdir /workdir && mkdir /build

# for clustermanagement
VOLUME [ "/workdir" ]


COPY . /build

# Build and cleanup
RUN cd /build && /bin/bash build.sh && cp -a /build/ClusterManagement/bin/Release /app && cd /app && rm -rf /build && rm -rf /root/.nuget && rm -rf /root/.local/share/NuGet/Cache && rm -rf /tmp/* && rm -rf /root/.cache

WORKDIR /workdir
ENTRYPOINT [ "mono", "--debug", "/app/ClusterManagement.exe" ]

# docker build --squash . -t matthid/clustermanagement:latest -t matthid/clustermanagement:0.3.3 && docker push matthid/clustermanagement:0.3.3 && docker push matthid/clustermanagement:latest
# MSYS_NO_PATHCONV=1 docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v $PROJDIR/clustercgf:/clustercfg -v $PROJDIR:/workdir matthid/clustermanagement
