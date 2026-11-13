package edu.firefighting.stickies;

import android.annotation.SuppressLint;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanRecord;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.os.Build;
import android.os.ParcelUuid;
import android.os.SystemClock;
import android.util.Log;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

/**
 * Minimal BLE central for Sensorimotor Stickies feedback devices, driven from Unity
 * (Assets/Scripts/Haptics/Stickies/AndroidStickyTransport.cs).
 *
 * Mirrors the StickyDebugger reference app: devices advertise a name starting with "STICKY",
 * the feedback service and Motor Command characteristic share the "stickySTICKY" base UUID,
 * and motor commands are written with response.
 *
 * Android allows one outstanding GATT operation per connection, so each device keeps at most
 * one pending command per motor: a newer command replaces one that has not been sent yet.
 * Unity polls state with getDevices() instead of receiving callbacks.
 */
@SuppressLint("MissingPermission") // Unity asks for BLUETOOTH_SCAN / BLUETOOTH_CONNECT before creating this.
public class StickiesBle {
    private static final String TAG = "Stickies";

    private static final UUID FEEDBACK_SERVICE = UUID.fromString("00020000-7374-6963-6b79-535449434b59");
    private static final UUID MOTOR_COMMAND = UUID.fromString("00020001-7374-6963-6b79-535449434b59");
    private static final UUID SENSING_SERVICE = UUID.fromString("00010000-7374-6963-6b79-535449434b59");

    private static final int DISCOVERED = 0;
    private static final int CONNECTING = 1;
    private static final int READY = 2;
    private static final int DISCONNECTED = 3;

    private static final long WRITE_TIMEOUT_MS = 1000;

    private final Context context;
    private final BluetoothAdapter adapter;
    private final Map<String, Device> devices = new LinkedHashMap<>();
    private final StringBuilder log = new StringBuilder();
    private BluetoothLeScanner scanner;
    private boolean scanning;
    private String status = "Idle";

    private static final class Device {
        final String address;
        String name;
        int rssi;
        int state = DISCOVERED;
        BluetoothDevice device;
        BluetoothGatt gatt;
        BluetoothGattCharacteristic motor;
        final LinkedHashMap<Integer, byte[]> pending = new LinkedHashMap<>();
        boolean writing;
        long writeStartedAt;

        Device(String address) {
            this.address = address;
        }
    }

    public StickiesBle(Context context) {
        this.context = context.getApplicationContext();
        BluetoothManager manager = (BluetoothManager) this.context.getSystemService(Context.BLUETOOTH_SERVICE);
        this.adapter = manager != null ? manager.getAdapter() : null;
        status = adapter == null ? "This device has no Bluetooth adapter"
                : adapter.isEnabled() ? "Bluetooth ready" : "Bluetooth is off. Turn it on in the headset settings.";
    }

    // ---------------------------------------------------------------- Unity API

    public synchronized String getStatus() {
        return status;
    }

    /** One device per line: address|name|rssi|state. */
    public synchronized String getDevices() {
        StringBuilder sb = new StringBuilder();
        for (Device d : devices.values()) {
            sb.append(d.address).append('|')
              .append(d.name == null ? "" : d.name.replace('|', '/').replace('\n', ' ')).append('|')
              .append(d.rssi).append('|')
              .append(d.state).append('\n');
        }
        return sb.toString();
    }

    public synchronized String drainLog() {
        String text = log.toString();
        log.setLength(0);
        return text;
    }

    public synchronized void startScan() {
        if (scanning) return;
        if (adapter == null || !adapter.isEnabled()) {
            status = adapter == null ? "This device has no Bluetooth adapter" : "Bluetooth is off. Turn it on in the headset settings.";
            return;
        }
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) {
            status = "BLE scanner unavailable";
            return;
        }
        ScanSettings settings = new ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .build();
        try {
            scanner.startScan(null, settings, scanCallback);
            scanning = true;
            status = "Scanning for feedback Stickies";
            note("Scan started");
        } catch (Exception e) {
            status = "Scan failed: " + e.getMessage();
            note(status);
        }
    }

    public synchronized void stopScan() {
        if (!scanning || scanner == null) return;
        try {
            scanner.stopScan(scanCallback);
        } catch (Exception e) {
            note("stopScan: " + e.getMessage());
        }
        scanning = false;
        status = "Scan stopped";
        note("Scan stopped");
    }

    public synchronized void connect(String address) {
        if (adapter == null) return;
        Device d = devices.get(address);
        if (d == null) {
            d = new Device(address);
            devices.put(address, d);
        }
        if (d.state == CONNECTING || d.state == READY) return;
        if (d.device == null) {
            try {
                d.device = adapter.getRemoteDevice(address);
            } catch (IllegalArgumentException e) {
                note("Bad address " + address);
                return;
            }
        }
        closeGatt(d);
        d.state = CONNECTING;
        d.pending.clear();
        d.writing = false;
        note("Connecting to " + label(d));
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                d.gatt = d.device.connectGatt(context, false, gattCallback, BluetoothDevice.TRANSPORT_LE);
            } else {
                d.gatt = d.device.connectGatt(context, false, gattCallback);
            }
        } catch (Exception e) {
            d.state = DISCONNECTED;
            note("connectGatt failed for " + label(d) + ": " + e.getMessage());
        }
    }

    public synchronized void disconnect(String address) {
        Device d = devices.get(address);
        if (d == null) return;
        closeGatt(d);
        d.state = DISCONNECTED;
    }

    /**
     * Queues a Motor Command. payloadHex is the raw characteristic value as hex
     * (see StickyProtocol.cs). Returns false when the device is not connected.
     */
    public synchronized boolean write(String address, String payloadHex) {
        Device d = devices.get(address);
        if (d == null || d.state != READY || d.gatt == null || d.motor == null) return false;
        byte[] payload = fromHex(payloadHex);
        if (payload.length < 2) return false;

        int motorId = payload[1] & 0xFF;
        if (motorId == 0xFF) d.pending.clear(); // "all motors" supersedes anything queued
        d.pending.remove(motorId);
        d.pending.put(motorId, payload);

        if (d.writing && SystemClock.elapsedRealtime() - d.writeStartedAt > WRITE_TIMEOUT_MS) {
            note("Write timed out on " + label(d));
            d.writing = false;
        }
        if (!d.writing) pump(d);
        return true;
    }

    public synchronized void close() {
        stopScan();
        for (Device d : devices.values()) {
            if (d.state == READY && d.gatt != null && d.motor != null) {
                // Best effort: silence every motor before dropping the link.
                writeNow(d, new byte[] {0x00, (byte) 0xFF});
            }
            closeGatt(d);
            d.state = DISCONNECTED;
        }
        devices.clear();
    }

    // ---------------------------------------------------------------- internals

    private void pump(Device d) {
        if (d.pending.isEmpty() || d.gatt == null || d.motor == null) {
            d.writing = false;
            return;
        }
        Integer key = d.pending.keySet().iterator().next();
        byte[] payload = d.pending.remove(key);
        d.writing = writeNow(d, payload);
        d.writeStartedAt = SystemClock.elapsedRealtime();
        if (!d.writing) {
            // The stack refused this write (busy). Keep it unless something newer replaced it;
            // the next write() call retries.
            if (!d.pending.containsKey(key)) d.pending.put(key, payload);
            note("Write refused on " + label(d));
        }
    }

    @SuppressWarnings("deprecation")
    private boolean writeNow(Device d, byte[] payload) {
        try {
            if (Build.VERSION.SDK_INT >= 33) {
                return d.gatt.writeCharacteristic(d.motor, payload,
                        BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) == 0; // BluetoothStatusCodes.SUCCESS
            }
            d.motor.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);
            d.motor.setValue(payload);
            return d.gatt.writeCharacteristic(d.motor);
        } catch (Exception e) {
            note("Write failed on " + label(d) + ": " + e.getMessage());
            return false;
        }
    }

    private void closeGatt(Device d) {
        if (d.gatt != null) {
            try {
                d.gatt.disconnect();
                d.gatt.close();
            } catch (Exception ignored) {
            }
        }
        d.gatt = null;
        d.motor = null;
        d.writing = false;
        d.pending.clear();
    }

    private Device byGatt(BluetoothGatt gatt) {
        String address = gatt.getDevice().getAddress();
        Device d = devices.get(address);
        return d != null && d.gatt == gatt ? d : null;
    }

    private void note(String message) {
        Log.i(TAG, message);
        if (log.length() < 8000) log.append(message).append('\n');
    }

    private static String label(Device d) {
        return (d.name != null && !d.name.isEmpty() ? d.name : "Sticky") + " (" + d.address + ")";
    }

    private static byte[] fromHex(String hex) {
        if (hex == null) return new byte[0];
        int n = hex.length() / 2;
        byte[] out = new byte[n];
        for (int i = 0; i < n; i++) {
            out[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
        }
        return out;
    }

    private static boolean isFeedbackSticky(String name, List<ParcelUuid> services) {
        if (name == null || !name.toUpperCase(Locale.US).startsWith("STICKY")) return false;
        if (services != null) {
            for (ParcelUuid uuid : services) {
                if (FEEDBACK_SERVICE.equals(uuid.getUuid())) return true;
                if (SENSING_SERVICE.equals(uuid.getUuid())) return false;
            }
        }
        String upper = name.toUpperCase(Locale.US);
        if (upper.contains("SENSE")) return false;
        return upper.contains("FB") || upper.contains("FEEDBACK");
    }

    // ---------------------------------------------------------------- callbacks

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        public void onScanResult(int callbackType, ScanResult result) {
            BluetoothDevice device = result.getDevice();
            ScanRecord record = result.getScanRecord();
            String name = record != null ? record.getDeviceName() : null;
            if (name == null) {
                try {
                    name = device.getName();
                } catch (SecurityException ignored) {
                }
            }
            List<ParcelUuid> services = record != null ? record.getServiceUuids() : null;
            if (!isFeedbackSticky(name, services)) return;

            synchronized (StickiesBle.this) {
                String address = device.getAddress();
                Device d = devices.get(address);
                if (d == null) {
                    d = new Device(address);
                    devices.put(address, d);
                    note("Found " + name + " (" + address + ")");
                }
                d.name = name;
                d.rssi = result.getRssi();
                d.device = device;
            }
        }

        @Override
        public void onScanFailed(int errorCode) {
            synchronized (StickiesBle.this) {
                scanning = false;
                status = "Scan failed (error " + errorCode + ")";
                note(status);
            }
        }
    };

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt gatt, int statusCode, int newState) {
            synchronized (StickiesBle.this) {
                Device d = byGatt(gatt);
                if (d == null) {
                    gatt.close();
                    return;
                }
                if (newState == BluetoothProfile.STATE_CONNECTED && statusCode == BluetoothGatt.GATT_SUCCESS) {
                    note("Connected to " + label(d) + ", discovering services");
                    if (!gatt.discoverServices()) {
                        note("discoverServices refused for " + label(d));
                        closeGatt(d);
                        d.state = DISCONNECTED;
                    }
                } else {
                    note("Disconnected from " + label(d) + " (status " + statusCode + ")");
                    closeGatt(d);
                    d.state = DISCONNECTED;
                }
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt gatt, int statusCode) {
            synchronized (StickiesBle.this) {
                Device d = byGatt(gatt);
                if (d == null) return;
                BluetoothGattService service = gatt.getService(FEEDBACK_SERVICE);
                d.motor = service != null ? service.getCharacteristic(MOTOR_COMMAND) : null;
                if (statusCode != BluetoothGatt.GATT_SUCCESS || d.motor == null) {
                    note(label(d) + " has no feedback Motor Command characteristic (is it a sensing Sticky?)");
                    closeGatt(d);
                    d.state = DISCONNECTED;
                    return;
                }
                gatt.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH);
                d.state = READY;
                d.writing = false;
                note(label(d) + " ready");
            }
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, int statusCode) {
            synchronized (StickiesBle.this) {
                Device d = byGatt(gatt);
                if (d == null) return;
                if (statusCode != BluetoothGatt.GATT_SUCCESS) note("Write status " + statusCode + " on " + label(d));
                d.writing = false;
                pump(d);
            }
        }
    };
}
