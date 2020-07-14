#!/bin/bash

CORERT=../../../
ILCPATH=$CORERT/bin/OSX.x64.Debug
LLVMPATH=/usr/local/opt/llvm

rm -rf bin obj
mkdir -p bin obj

#echo
#echo "Compile CoreRT in debug mode"
#pushd $CORERT && ./build.sh debug skiptests && popd || exit -1

echo
echo "Compiling C# -> IL..."
dotnet $CORERT/Tools/csc.exe /noconfig /nostdlib /runtimemetadataversion:v4.0.30319 zerosharp.cs /out:obj/zerosharp.ilexe /langversion:latest /unsafe || exit -1

echo
echo "Compiling IL to LLVM bitcode..."
$ILCPATH/tools/ilc --verbose --singlethreaded obj/zerosharp.ilexe -o obj/zerosharp.bc --systemmodule zerosharp --map obj/zerosharp.map || exit -1

echo
echo "Extracting bitcode to readable LL..."
$LLVMPATH/bin/llvm-dis obj/zerosharp.bc

echo
echo "Linking..."
#Apparently this needs LLVM 10, can't be 11
$LLVMPATH/bin/clang -arch i386 -isysroot /Library/Developer/CommandLineTools/SDKs/MacOSX10.13.sdk -L/Library/Developer/CommandLineTools/SDKs/MacOSX10.13.sdk/usr/lib -L/Library/Developer/CommandLineTools/SDKs/MacOSX10.13.sdk/usr/lib/system -e___managed__Main obj/zerosharp.bc -o bin/zerosharp || exit -1


echo
echo "Calling zerosharp..."

./bin/zerosharp
RES=$?

echo
echo "Result: $RES"
