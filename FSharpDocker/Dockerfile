# Install all required libs
FROM fsharp:4.1.18

RUN apt-get update && apt-get install -y docker.io curl && apt-get clean && rm -rf /var/lib/apt/lists
RUN curl -L https://github.com/docker/machine/releases/download/v0.9.0-rc2/docker-machine-`uname -s`-`uname -m` >/tmp/docker-machine && \
    chmod +x /tmp/docker-machine && \
    sudo cp /tmp/docker-machine /usr/local/bin/docker-machine

# docker build --squash ./FSharpDocker -t matthid/fsharp-docker:latest -t matthid/fsharp-docker:0.1.0 && docker push matthid/fsharp-docker:0.1.0 && docker push matthid/fsharp-docker:latest