#!/bin/bash

glslangValidator preprocess.vert.glsl -V -l -o preprocess.vert.glsl.spv
glslangValidator preprocess.frag.glsl -V -l -o preprocess.frag.glsl.spv