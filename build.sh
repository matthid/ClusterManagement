#!/bin/bash

if test "$OS" = "Windows_NT"
then
  # use .Net

  ./paket.exe restore
  exit_code=$?
  if [ $exit_code -ne 0 ]; then
  	exit $exit_code
  fi
  
  [ ! -e build.fsx ] && ./paket.exe update
  [ ! -e build.fsx ] && packages/build/FAKE/tools/FAKE.exe init.fsx
  packages/build/FAKE/tools/FAKE.exe $@ --fsiargs -d:MONO build.fsx 
else
  # use mono
  # workaround https://github.com/fsharp/FAKE/pull/1578
  MSBuild="msbuild"
  mono paket.exe restore
  exit_code=$?
  if [ $exit_code -ne 0 ]; then
  	exit $exit_code
  fi

  [ ! -e build.fsx ] && mono ./paket.exe update
  [ ! -e build.fsx ] && mono packages/build/FAKE/tools/FAKE.exe init.fsx
  mono packages/build/FAKE/tools/FAKE.exe $@ --fsiargs -d:MONO build.fsx 
fi
