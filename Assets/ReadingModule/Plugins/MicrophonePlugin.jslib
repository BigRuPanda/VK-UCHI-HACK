/**
 * MicrophonePlugin.jslib
 * Copied from unity-webgl-microphone-master (MIT License, Copyright 2018 Razer, Inc.)
 * https://github.com/adrenak/univoice
 *
 * Provides real audio capture via getUserMedia + AudioContext + AnalyserNode.
 * C# polls GetMicrophoneVolume() every frame via Microphone.Update().
 *
 * Modified:
 *   - Added SendMessage callback 'ReceivePermissionResult' to SpeechBridge on success/failure
 *   - Replaced deprecated navigator.getUserMedia with navigator.mediaDevices.getUserMedia
 *   - Added window._micStream storage for reuse by SpeechRecognizer
 */

var MicrophonePlugin = {

  buffer: undefined,

  Init: function() {
    console.log('[MicrophonePlugin] Init');

    // Buffer for volume reading
    document.volume = 0;
    var byteOffset = 0;
    var length = 1024;
    this.buffer = new ArrayBuffer(4 * length);
    document.dataArray = new Float32Array(this.buffer, byteOffset, length);

    // Use modern API (navigator.mediaDevices.getUserMedia)
    var getUserMedia = (navigator.mediaDevices && navigator.mediaDevices.getUserMedia)
      ? function(constraints, success, error) {
          navigator.mediaDevices.getUserMedia(constraints).then(success).catch(error);
        }
      : function(constraints, success, error) {
          var legacyGUM = navigator.getUserMedia
            || navigator.webkitGetUserMedia
            || navigator.mozGetUserMedia;
          if (legacyGUM) {
            legacyGUM.call(navigator, constraints, success, error);
          } else {
            error(new Error('getUserMedia not supported'));
          }
        };

    getUserMedia(
      { audio: true, video: false },
      function(stream) {
        console.log('[MicrophonePlugin] getUserMedia success:', stream);

        // Store stream globally so SpeechRecognizer.jslib can reuse it
        window._micStream = stream;

        document.position = 0;
        document.audioContext = new AudioContext();
        document.tempSize = 1024;
        document.tempArray = new Float32Array(document.tempSize);
        document.analyser = document.audioContext.createAnalyser();
        document.analyser.minDecibels = -90;
        document.analyser.maxDecibels = -10;
        document.analyser.smoothingTimeConstant = 0.85;

        document.source = document.audioContext.createMediaStreamSource(stream);
        document.source.connect(document.analyser);

        document.readDataOnInterval = function() {
          if (document.dataArray == undefined) {
            setTimeout(document.readDataOnInterval, 250);
            return;
          }

          document.tempInterval = Math.floor(
            document.tempSize / document.dataArray.length * 250
          );
          setTimeout(document.readDataOnInterval, document.tempInterval);

          if (document.dataArray == undefined) return;

          document.analyser.getFloatTimeDomainData(document.tempArray);

          document.volume = 0;
          var j = (document.position + document.dataArray.length - document.tempSize)
                  % document.dataArray.length;
          for (var i = 0; i < document.tempSize; ++i) {
            document.volume = Math.max(document.volume, Math.abs(document.tempArray[i]));
            document.dataArray[j] = document.tempArray[i];
            j = (j + 1) % document.dataArray.length;
          }
          document.position = (document.position + document.tempSize)
                               % document.dataArray.length;
        };

        document.readDataOnInterval();

        // Now that permission is granted, enumerate devices (labels are only
        // available after getUserMedia succeeds — calling before gives empty labels)
        if (navigator.mediaDevices && navigator.mediaDevices.enumerateDevices) {
          navigator.mediaDevices.enumerateDevices().then(function(devices) {
            document.mMicrophones = [];
            devices.forEach(function(device) {
              if (device.kind === 'audioinput') {
                document.mMicrophones.push(
                  device.label || ('Microphone ' + document.mMicrophones.length)
                );
              }
            });
            console.log('[MicrophonePlugin] Enumerated', document.mMicrophones.length, 'audio input(s).');
          }).catch(function(err) {
            console.warn('[MicrophonePlugin] enumerateDevices error:', err.name);
          });
        }

        // Notify SpeechBridge that permission was granted
        SendMessage('SpeechBridge', 'ReceivePermissionResult', 'granted');
      },
      function(error) {
        console.error('[MicrophonePlugin] getUserMedia error:', error.name, error.message);
        SendMessage('SpeechBridge', 'ReceivePermissionResult', 'denied:' + error.name);
      }
    );
  },

  QueryAudioInput: function() {
    // No-op: device enumeration is now done inside Init() after getUserMedia succeeds,
    // because device labels are only available after permission is granted.
    console.log('[MicrophonePlugin] QueryAudioInput (deferred to Init success callback).');
  },

  // Returns document.volume directly — does NOT depend on device enumeration.
  // Use this for real-time level polling instead of GetMicrophoneVolume(index).
  GetMicVolumeDirect: function() {
    if (typeof document.volume === 'undefined') return 0;
    return document.volume;
  },

  GetNumberOfMicrophones: function() {
    var microphones = document.mMicrophones;
    if (microphones == undefined) return 0;
    return microphones.length;
  },

  GetMicrophoneDeviceName: function(index) {
    var returnStr = 'Unknown';
    var microphones = document.mMicrophones;
    if (microphones != undefined && index >= 0 && index < microphones.length) {
      if (microphones[index] != undefined) {
        returnStr = microphones[index];
      }
    }
    var len = lengthBytesUTF8(returnStr) + 1;
    var buffer = _malloc(len);
    stringToUTF8(returnStr, buffer, len);
    return buffer;
  },

  GetMicrophoneVolume: function(index) {
    if (document.volume == undefined) return 0;
    return document.volume;
  }

};

mergeInto(LibraryManager.library, MicrophonePlugin);
