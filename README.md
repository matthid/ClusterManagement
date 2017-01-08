ClusterManagement
===================
## [Documentation](https://matthid.github.io/ClusterManagement/)

[![Join the chat at https://gitter.im/matthid/Yaaf](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/matthid/Yaaf?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

## Build status

**Development Branch**

[![Build Status](https://travis-ci.org/matthid/ClusterManagement.svg?branch=develop)](https://travis-ci.org/matthid/ClusterManagement)
[![Build status](https://ci.appveyor.com/api/projects/status/2xitdogybhrpd74o/branch/develop?svg=true)](https://ci.appveyor.com/project/matthid/yaaf-511/branch/develop)

**Master Branch**

[![Build Status](https://travis-ci.org/matthid/ClusterManagement.svg?branch=master)](https://travis-ci.org/matthid/ClusterManagement)
[![Build status](https://ci.appveyor.com/api/projects/status/2xitdogybhrpd74o/branch/master?svg=true)](https://ci.appveyor.com/project/matthid/yaaf-511/branch/master)

## Docker

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The ClusterManagement tool can be <a href="https://hub.docker.com/r/matthid/clustermanagement/">used immediately with docker</a>:
      <pre>PS> docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v C:\MyProjects\MyInfrastructure\clustercgf:/clustercfg -v "$pwd:/workdir" -ti matthid/clustermanagement --help</pre>
      <pre>$   docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v C:\MyProjects\MyInfrastructure\clustercgf:/clustercfg -v "`pwd`:/workdir" -ti matthid/clustermanagement --help</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

## Why ClusterManagement?

CloudManagement helps to create an automatic and integrated CI-Pipeline right to the cloud based on docker images.
It helps to handle:

    runtime-configuration (vault/consul)
    secrets (vault)
    cluster duplication for testing
    storage (flocker)

It deploys cluster on top of docker/docker-machine/docker-swarm and is therefore not coupled with a specific cloud provider. Though currently only aws is supported.

While at the same time keeps the manual intervention to a minimum.

This project popped out of an idea to move all services of a peronally managed (virtual) root server to the cloud.
The idea was to make the upgrade-mess a lot more testable and to quickly create test environments when I need them.
First I started with quite a lot of bash scripts which became unreadable and unmanagable very quickly.

## Quick intro

We will shorten the above command line to "cm <args>" instead of the huge docker command line options. As an alternative you can use the binary version or build yourself.
However I personally always use the docker version therefore this is currently the only "tested" release.

Now start by creating a new cluster:

```bash
cm cluster createnew -c my-cluster --secret test123 --masternodes 3 --workernodes 5 --masterasworker
```

This will create a new cluster-configuration and make it initially available for ClusterManagement. It will not however setup any machines and is a local-only command.

As we currently only support AWS you need to setup some aws configuration in your cluster:

```
cm config set -c my-cluster --key AWS_ZONE --value eu-central-1a
cm config set -c my-cluster --key AWS_REGION --value eu-central-1
cm config set -c my-cluster --key AWS_ACCESS_KEY_ID --value <aws-key-id>
cm config set -c my-cluster --key AWS_ACCESS_KEY_SECRET --value <aws-key-secret>
```


Now that we setup the initial configuration for the "my-cluster" cluster we can let clustermanagement create it for you:

```
cm cluster init -c my-cluster
```

This will create 8 machines on amazon (via docker-machine) and provision them (via 'docker-machine ssh machine sudo docker ...'):
 - Create the machines
 - Setup flocker
 - Setup consul
 - Setup vault

This way you have a cluster - ready to be deployed with software.

ClusterManagement can manage a whole cluster for you and make it simple to manage it:

```
cm docker-machine -c my-cluster -- ls
cm docker-machine -c my-cluster -- ssh my-cluster-master-01
```

These commands show that you can still access low-level `docker-machine` functionality.


You can now write short deployment scripts to setup your software and use ClusterManagement to deploy it:
 
```
cm deploy --cluster my-cluster --script my_deployment_script.fsx -- <your_script_args>
```

Now you should have enough info and git an idea how easy it is to use ClusterManagement to automatically deploy infrastructure
and test your software before deploying it to release.



## CI Support

```
cm config copy --source source-cluster --dest dest-cluster
```
Copy exiting configuration from one cluster to another. This way you can store a set of configuration in the repository and only need a single
secret on the CI.


 --- (TO BE IMPLEMENTED) ---

```
cm volume clone --source source-cluster --dest dest-cluster
```
Copy existing volumes from one cluster to another.
This command can be used for a lot of scenarios:

 - Test Upgrade-Behaviour of production data in CI
 - Clone an environment for local use.
 - ...

## Technical details

ClusterManagement is basically a wrapper around `docker` and `docker-machine`.
It understands and manages [docker-swarm](https://www.docker.com/products/docker-swarm), [flocker](https://clusterhq.com/flocker/introduction/), [vault](https://www.vaultproject.io/) and [consul](https://www.consul.io/).

ClusterManagement will manage two folders in your "C:\MyProjects\MyInfrastructure\clustercgf" folder.
 * .cm
   This contains the cluster configurations and is encrypted. This way you can add this folder to your repository and keep it safe.
 * .cm-temp
   This contains local-only files which should NOT be added to your repository (use git-ignore files to ignore it.).
   For example your cluster-secrets will be saved in this folder.
   