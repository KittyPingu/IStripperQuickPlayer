"""Detect lost combined foreground before requesting a backward SAM2 pass."""
import cv2,numpy as np
LOSS_FRACTION=.06
MIN_COMPONENT_FRACTION=.003
PERSISTENCE=3
HOLD_FRAMES=32
PHOTO_DELTA=25
END_MISSING_FRACTION=.04

def largest_component(mask):
    count,_,stats,_=cv2.connectedComponentsWithStats(mask.astype(np.uint8),8)
    return int(stats[1:,cv2.CC_STAT_AREA].max()) if count>1 else 0

def mapping(previous,current):
    flow=cv2.calcOpticalFlowFarneback(current,previous,None,.5,3,15,3,5,1.2,0)
    y,x=np.indices(current.shape,dtype=np.float32)
    coordinates=np.dstack((x,y))+flow
    warped=cv2.remap(previous,coordinates,None,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=255)
    consistent=np.abs(current.astype(np.int16)-warped.astype(np.int16))<=PHOTO_DELTA
    return coordinates,consistent

class CombinedCollapseDetector:
    def __init__(self):self.reference=None;self.run=0;self.held=0;self.events=[]
    def step(self,current,coordinates,consistent,index):
        current=current.astype(bool)
        record=dict(frame=int(index),missingFraction=0.,component=0,suspect=False,confirmed=False)
        if self.reference is not None:
            warped=cv2.remap(self.reference.astype(np.uint8),coordinates.astype(np.float32),None,cv2.INTER_NEAREST,borderMode=cv2.BORDER_CONSTANT)>0
            expected=warped&consistent
            dilated=cv2.dilate(current.astype(np.uint8),np.ones((3,3),np.uint8))>0
            missing=expected&~dilated
            fraction=float(missing.sum()/max(1,expected.sum()));component=largest_component(missing)
            suspect=fraction>=LOSS_FRACTION and component>=max(6,MIN_COMPONENT_FRACTION*current.size)
            self.run=self.run+1 if suspect else 0
            self.held+=1
            record.update(missingFraction=fraction,component=component,suspect=bool(suspect),confirmed=self.run>=PERSISTENCE)
            self.reference=(expected|current) if self.held%HOLD_FRAMES else current
        else:self.reference=current
        self.events.append(record)
        return record

def endpoint_disagreement(forward,anchor):
    cover=cv2.dilate(forward.astype(np.uint8),np.ones((7,7),np.uint8))>0
    missing=anchor&~cover
    fraction=float(missing.sum()/max(1,anchor.sum()));component=largest_component(missing)
    return dict(missingFraction=fraction,component=component,significant=bool(fraction>=END_MISSING_FRACTION and component>=MIN_COMPONENT_FRACTION*anchor.size))

def decide(events,endpoint,reliable,same_scene):
    temporal=any(event['confirmed'] for event in events)
    reasons=[]
    if temporal:reasons.append('sustained motion-consistent foreground loss')
    if endpoint['significant']:reasons.append('missing foreground at next keyframe')
    if not same_scene:return dict(runBackward=False,reasons=['scene cut at next keyframe'])
    if not reliable:return dict(runBackward=False,reasons=['next keyframe failed person-confidence guard'])
    return dict(runBackward=bool(reasons),reasons=reasons or ['combined foreground remained covered'])

def self_test():
    shape=(32,48);y,x=np.indices(shape,dtype=np.float32);identity=np.dstack((x,y));valid=np.ones(shape,bool)
    body=np.zeros(shape,bool);body[5:26,6:39]=True
    # Losing a duplicate object has no effect when the combined mask is unchanged.
    stable=CombinedCollapseDetector()
    for i in range(8):stable.step(body,identity,valid,i)
    assert not any(e['confirmed'] for e in stable.events)
    broken=CombinedCollapseDetector();broken.step(body,identity,valid,0)
    for i in range(1,4):broken.step(np.zeros(shape,bool),identity,valid,i)
    assert broken.events[-1]['confirmed']
    shifted=np.zeros_like(body);shifted[:,3:]=body[:,:-3]
    moving=CombinedCollapseDetector();moving.step(body,identity,valid,0)
    moving.step(shifted,identity-np.array([3,0],np.float32),valid,1)
    assert not moving.events[-1]['suspect']
    occluded=CombinedCollapseDetector();occluded.step(body,identity,valid,0)
    for i in range(1,5):occluded.step(np.zeros(shape,bool),identity,np.zeros(shape,bool),i)
    assert not any(e['confirmed'] for e in occluded.events)
    zoom_coordinates=(identity-np.array([24,16],np.float32))*1.5+np.array([24,16],np.float32)
    zoomed=cv2.remap(body.astype(np.uint8),zoom_coordinates,None,cv2.INTER_NEAREST)>0
    zoom=CombinedCollapseDetector();zoom.step(body,identity,valid,0);zoom.step(zoomed,zoom_coordinates,valid,1)
    assert not zoom.events[-1]['suspect']
    flicker=CombinedCollapseDetector();flicker.step(body,identity,valid,0)
    flicker.step(np.zeros(shape,bool),identity,valid,1);flicker.step(body,identity,valid,2)
    assert not any(e['confirmed'] for e in flicker.events)
    gradual=CombinedCollapseDetector();gradual.step(body,identity,valid,0)
    for i in range(1,12):
        shrunk=body.copy();shrunk[:,:6+i]=False;gradual.step(shrunk,identity,valid,i)
    assert any(e['confirmed'] for e in gradual.events)
    good=endpoint_disagreement(body,body);bad=endpoint_disagreement(np.zeros_like(body),body)
    assert not good['significant'] and bad['significant']
    assert not decide(stable.events,good,True,True)['runBackward']
    assert decide(broken.events,good,True,True)['runBackward']
    assert decide(stable.events,bad,True,True)['runBackward']
    assert not decide(broken.events,bad,True,False)['runBackward']
    assert not decide(broken.events,bad,False,True)['runBackward']
    print('Selective collapse self-test passed',flush=True)

if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        parser.error('--self-test is required')
