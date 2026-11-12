# GPUse audio / 3D / video runners — added one at a time AFTER image works.
#
# Each runner will follow the exact same simple contract as image_ai.py:
#
#     python3 <runner>.py "<prompt>" <output-file>
#
# It does the real work on this host computer and saves ONE result file.
# agent.js already knows how to send that file back to the server.
#
# Planned order (easiest / most demoable first):
#   1. audio_ai.py    - text-to-music or text-to-speech (MusicGen / XTTS)
#   2. model3d_ai.py  - text/image-to-3D, saves a .glb or .obj file
#   3. video_ai.py    - text-to-video (heaviest, needs a strong GPU)
#
# Nothing in the server or agent has to change to add one:
# just add a new "else if (job.type === ...)" block in agent.js Step 5.
