% Gộp các đường biên thành 1 đường liên tục cho Robot SCARA
clear; clc; close all;

%% 1. Thông số hình học
L1 = 225; 
L2 = 175; 
% Số lượng điểm lấy mẫu cho mỗi đoạn
n = 100;

%% 2. Tính toán từng phân đoạn
% --- Đoạn 1: Biên ngoài (quét q1 từ 132 về -132, q2 = 0) ---
q1_d1 = deg2rad(linspace(132, -132, n));
x1 = L1*cos(q1_d1) + L2*cos(q1_d1);
y1 = L1*sin(q1_d1) + L2*sin(q1_d1);

% --- Đoạn 2: Cạnh trái (tại q1 = -132, q2 từ 0 xuống -150) ---
q1_2 = deg2rad(-132);
q2_d2 = deg2rad(linspace(0, -150, n));
x2_f = L1*cos(q1_2) + L2*cos(q1_2 + q2_d2);
y2_f = L1*sin(q1_2) + L2*sin(q1_2 + q2_d2);
% Lọc y <= 0
mask2 = y2_f <= 0.0001;
x2 = x2_f(mask2);
y2 = y2_f(mask2);

% --- Đoạn 3: Cạnh phải (tại q1 = 132, q2 từ 150 về 0) ---
% Lưu ý: Đảo thứ tự để khi nối mảng đường đi sẽ liên tục
q1_3 = deg2rad(132);
q2_d3 = deg2rad(linspace(150, 0, n));
x3_f = L1*cos(q1_3) + L2*cos(q1_3 + q2_d3);
y3_f = L1*sin(q1_3) + L2*sin(q1_3 + q2_d3);
% Lọc y >= 0
mask3 = y3_f >= -0.0001;
x3 = x3_f(mask3);
y3 = y3_f(mask3);

%% 3. Gộp các mảng (Thứ tự: Cạnh phải -> Cung ngoài -> Cạnh trái)
% Việc sắp xếp này tạo thành một đường bao hở bao quanh phía trước robot
X_total = [x3, x1, x2];
Y_total = [y3, y1, y2];

%% 4. BỔ SUNG: Thiết lập tọa độ lồng kính 660x660 mm
% Gốc O_f cách vách sau 166mm -> y_back = 166
% Vách trước: y_front = 166 - 670 = -504 (hoặc 166 - 660 = -494 đối với kích thước 660)
% Đặt chính giữa vách trái/phải -> x_left = -330, x_right = 330
y_box = [-330, 330, 330, -330, -330];
x_box = [-166, -166, 494, 494, -166];% Lồng kính 660x660mm

%% 5. Vẽ đồ thị
figure;
hold on;
box on;

% BỔ SUNG: Vẽ khung lồng kính 660x660mm
plot(x_box, y_box, 'r--', 'LineWidth', 2, 'DisplayName', 'Lồng kính (660x660)');

% Vẽ đường tổng hợp (Đường liền mạch)
plot(X_total, Y_total, 'b-', 'LineWidth', 2, 'DisplayName', 'Biên ngoài');

% Vẽ thêm đường 4 (Biên trong) để đối chiếu nếu cần
q1_4 = deg2rad(linspace(0, 360, 400));
x4 = L1*cos(q1_4) + L2*cos(q1_4 + deg2rad(150));
y4 = L1*sin(q1_4) + L2*sin(q1_4 + deg2rad(150));
plot(x4, y4, 'b--', 'LineWidth', 1, 'DisplayName', 'Biên trong');

% Trang trí
plot(0,0, 'ko', 'MarkerFaceColor', 'k', 'DisplayName', 'O_f'); % Gốc tọa độ
xlabel('x_f (mm)', 'FontName', 'Times New Roman', 'FontSize', 16, 'FontAngle', 'italic');
ylabel('y_f (mm)', 'FontName', 'Times New Roman', 'FontSize', 16, 'FontAngle', 'italic');
axis equal;
ax = gca;
grid on;
ax.LineWidth = 0.75; 
ax.FontName = 'Times New Roman';
ax.FontSize = 14;
ax.GridColor = [0 0 0]; 
ax.GridLineStyle = '--'; 
ax.GridAlpha = 1; 
ax.Layer = 'top';
legend('Location', 'northeast');