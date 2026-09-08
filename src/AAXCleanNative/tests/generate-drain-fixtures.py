#!/usr/bin/env python3
"""Tiny synthetic audio; installed FFmpeg is an independent encoder/decode path."""
from pathlib import Path
import hashlib,json,math,shutil,struct,subprocess,sys,wave

root=Path(sys.argv[1]).resolve()
root.mkdir(parents=True,exist_ok=True)
ffmpeg=shutil.which('ffmpeg')
if not ffmpeg: raise SystemExit('ffmpeg is required for the independent fixture path')
version=subprocess.check_output([ffmpeg,'-version'],text=True)
(root/'ffmpeg-version.txt').write_text(version)

def run(args):
    subprocess.run([ffmpeg,'-hide_banner','-loglevel','error','-y',*map(str,args)],check=True)

def packet_file(path,packets):
    with path.open('wb') as stream:
        for packet in packets:
            stream.write(struct.pack('<I',len(packet)))
            stream.write(packet)

manifest=[]
for codec,rate,length,out_rate in [('aac',44100,7201,16000),('eac3',48000,9809,32000)]:
    pcm=[]
    for sample in range(length):
        left=int(6000*math.sin(2*math.pi*641*sample/rate))
        right=int(4500*math.sin(2*math.pi*997*sample/rate))
        if sample in (117,length//2,length-29): left,right=23000,-21000
        pcm.extend((left,right))
    wav=root/f'{codec}.wav'
    with wave.open(str(wav),'wb') as stream:
        stream.setnchannels(2);stream.setsampwidth(2);stream.setframerate(rate)
        stream.writeframes(struct.pack('<'+'h'*len(pcm),*pcm))
    encoded=root/(codec+'.adts' if codec=='aac' else codec+'.eac3')
    run(['-i',wav,'-c:a',codec,'-b:a','128k' if codec=='aac' else '192k','-f','adts' if codec=='aac' else 'eac3',encoded])
    data=encoded.read_bytes();packets=[];offset=0;asc=None
    while offset<len(data):
        if codec=='aac':
            assert data[offset]==0xff and data[offset+1]&0xf6==0xf0
            header=7 if data[offset+1]&1 else 9
            size=((data[offset+3]&3)<<11)|(data[offset+4]<<3)|(data[offset+5]>>5)
            assert data[offset+6]&3==0, 'multiple ADTS raw blocks not generated'
            profile=(data[offset+2]>>6)+1;frequency=(data[offset+2]>>2)&15
            channels=((data[offset+2]&1)<<2)|(data[offset+3]>>6)
            config=bytes([(profile<<3)|(frequency>>1),((frequency&1)<<7)|(channels<<3)])
            assert asc is None or asc==config
            asc=config
            packets.append(data[offset+header:offset+size])
        else:
            assert data[offset:offset+2]==b'\x0b\x77'
            size=2*((((data[offset+2]&7)<<8)|data[offset+3])+1)
            packets.append(data[offset:offset+size])
        assert size>0 and offset+size<=len(data)
        offset+=size
    packet_file(root/f'{codec}.packets',packets)
    if asc: (root/'aac.asc').write_bytes(asc)
    if codec=='eac3':
        packet_file(root/'eac3-paired.packets',[b''.join(packets[i:i+2]) for i in range(0,len(packets),2)])
    reference=root/f'{codec}-reference.s16'
    run(['-i',encoded,'-map','0:a:0','-ac','2','-ar',out_rate,'-c:a','pcm_s16le','-f','s16le',reference])
    manifest.append(dict(codec=codec,inputRate=rate,inputSamples=length,outputRate=out_rate,
        packetCount=len(packets),referenceSamplesPerChannel=reference.stat().st_size//4,
        asc=asc.hex() if asc else None,source='generated sine plus three impulses; no provider media'))
manifest.append(dict(codec='ac4',status='no installed AC4 encoder; real coded-frame differential fixture not generated'))
# A real coded sample-rate change must be rejected before stale resampling.
changed=root/'eac3-32000.eac3'
run(['-i',root/'eac3.wav','-ar','32000','-ac','2','-c:a','eac3','-b:a','192k','-f','eac3',changed])
data=changed.read_bytes();packets=[];offset=0
while offset<len(data):
    assert data[offset:offset+2]==b'\x0b\x77'
    size=2*((((data[offset+2]&7)<<8)|data[offset+3])+1)
    assert size>0 and offset+size<=len(data)
    packets.append(data[offset:offset+size]);offset+=size
packet_file(root/'eac3-32000.packets',packets)
(root/'eac3-rate-change.packets').write_bytes((root/'eac3.packets').read_bytes()+(root/'eac3-32000.packets').read_bytes())
manifest.append(dict(codec='eac3-rate-change',expected='terminal unsupported decoded-format error',inputRates=[48000,32000]))
files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.iterdir()) if p.is_file()}
(root/'manifest.json').write_text(json.dumps(dict(fixtures=manifest,files=files),indent=2)+'\n')
print(json.dumps(manifest,indent=2))
