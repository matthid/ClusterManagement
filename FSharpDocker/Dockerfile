﻿# Install all required libs
FROM buildpack-deps:trusty

# modified from https://github.com/fsprojects/docker-fsharp/blob/master/4.1.18/mono/Dockerfile
# and https://github.com/mono/docker/blob/5d27cb132d84e12b8d983f4690c1ef716dc177db/5.0.0.100/Dockerfile
ENV MONO_THREADS_PER_CPU 50
RUN MONO_VERSION=5.2.0 && \
	DOCKER_CHANNEL=stable \
	DOCKER_VERSION=17.06.1-ce \
	DOCKER_MACHINE_VERSION=v0.12.2 \
    FSHARP_VERSION=4.1.18 && \
    FSHARP_PREFIX=/usr && \
    FSHARP_GACDIR=/usr/lib/mono/gac && \
    FSHARP_BASENAME=fsharp-$FSHARP_VERSION && \
    FSHARP_ARCHIVE=$FSHARP_VERSION.tar.gz && \
    FSHARP_ARCHIVE_URL=https://github.com/fsharp/fsharp/archive/$FSHARP_VERSION.tar.gz && \
    # See http://download.mono-project.com/repo/debian/dists/wheezy/snapshots/
    apt-key adv --keyserver hkp://p80.pool.sks-keyservers.net:80 --recv-keys 3FA7E0328081BFF6A14DA29AA6A19B38D3D831EF && \
    echo "deb http://download.mono-project.com/repo/debian wheezy/snapshots/$MONO_VERSION main" > /etc/apt/sources.list.d/mono-xamarin.list && \
    apt-get update -y && \
    apt-get --no-install-recommends install -y tar autoconf libtool pkg-config make git automake curl nuget mono-devel msbuild ca-certificates-mono && \
    rm -rf /var/lib/apt/lists/* && \
	# Install F#
    mkdir -p /tmp/src && \
    cd /tmp/src && \
    curl -LO $FSHARP_ARCHIVE_URL && \
    tar xf $FSHARP_ARCHIVE && \
    cd $FSHARP_BASENAME && \
    ./autogen.sh --prefix=$FSHARP_PREFIX --with-gacdir=$FSHARP_GACDIR && \
    make && \
    make install && \
    cd ~ && \
	# Install docker, see https://github.com/docker-library/docker/blob/5a196cae40e2a0ab5050cf6d79b697e032352b24/17.05/Dockerfile
	curl -fL -o docker.tgz "https://download.docker.com/linux/static/${DOCKER_CHANNEL}/x86_64/docker-${DOCKER_VERSION}.tgz" && \
	tar --extract --file docker.tgz --strip-components 1 --directory /usr/local/bin/ && \
	rm docker.tgz && \
	# Install docker-machine
    curl -L https://github.com/docker/machine/releases/download/${DOCKER_MACHINE_VERSION}/docker-machine-`uname -s`-`uname -m` >/tmp/docker-machine && \
    chmod +x /tmp/docker-machine && \
    sudo cp /tmp/docker-machine /usr/local/bin/docker-machine && \
    rm -rf /tmp/src /tmp/NuGetScratch ~/.nuget ~/.config ~/.local && \
    apt-get purge -y autoconf libtool make git automake curl

# docker build --squash ./FSharpDocker -t matthid/fsharp-docker:latest -t matthid/fsharp-docker:0.3.0 && docker push matthid/fsharp-docker:0.3.0 && docker push matthid/fsharp-docker:latest