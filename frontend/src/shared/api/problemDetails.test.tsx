import { render, screen } from '@testing-library/react';
import { Form, Input, InputNumber, type FormInstance } from 'antd';
import { act } from 'react';
import { describe, expect, it } from 'vitest';
import i18n from '@/shared/i18n';
import { applyProblemToForm, parseFieldPath, translateApiError } from './problemDetails';
import type { ApiError } from './types';

interface OrderForm {
  externalOrderId: string;
  items: { productId: string; stock: number }[];
}

/** A form with a top-level field and a nested list field, as the order-entry page will have. */
function renderOrderForm() {
  let form!: FormInstance<OrderForm>;

  function TestForm() {
    [form] = Form.useForm<OrderForm>();
    return (
      <Form form={form} initialValues={{ externalOrderId: 'WEB-1', items: [{ productId: 'p-1', stock: 0 }] }}>
        <Form.Item name="externalOrderId" label="externalOrderId">
          <Input />
        </Form.Item>
        <Form.List name="items">
          {(fields) =>
            fields.map((field) => (
              <Form.Item key={field.key} name={[field.name, 'stock']} label="stock">
                <InputNumber />
              </Form.Item>
            ))
          }
        </Form.List>
      </Form>
    );
  }

  render(<TestForm />);
  return form;
}

function validationError(errors: Record<string, string[]>): ApiError {
  return { status: 400, correlationId: 'corr-1', problem: { status: 400, errorCode: 'validation_failed', errors } };
}

describe('parseFieldPath', () => {
  it.each([
    ['password', ['password']],
    ['items[0].stock', ['items', 0, 'stock']],
    ['items[12].productId', ['items', 12, 'productId']],
    ['matrix[1][2]', ['matrix', 1, 2]],
    ['address.street', ['address', 'street']],
  ])('%s', (key, expected) => {
    expect(parseFieldPath(key)).toEqual(expected);
  });
});

describe('applyProblemToForm', () => {
  it('puts validation errors on their fields, including a nested path, and returns unknown keys', async () => {
    const form = renderOrderForm();
    let rest: string[] = [];

    act(() => {
      rest = applyProblemToForm(
        form,
        validationError({
          externalOrderId: ['Mã đơn đã tồn tại.'],
          'items[0].stock': ['Số lượng phải lớn hơn 0.'],
          customerId: ['Khách hàng không tồn tại.'],
        }),
        i18n.t,
      );
    });

    expect(form.getFieldError('externalOrderId')).toEqual(['Mã đơn đã tồn tại.']);
    expect(form.getFieldError(['items', 0, 'stock'])).toEqual(['Số lượng phải lớn hơn 0.']);
    // Shown under the right inputs (AntD renders field errors a moment later) ...
    expect(await screen.findByText('Mã đơn đã tồn tại.')).toBeInTheDocument();
    expect(await screen.findByText('Số lượng phải lớn hơn 0.')).toBeInTheDocument();
    // ... and the key that matches no field of this form is for the form-level alert.
    expect(rest).toEqual(['Khách hàng không tồn tại.']);
    expect(screen.queryByText('Khách hàng không tồn tại.')).not.toBeInTheDocument();
  });

  it('puts an error code on its field when the form says where it belongs, translated', () => {
    const form = renderOrderForm();
    const conflict: ApiError = { status: 409, correlationId: 'c', problem: { errorCode: 'user_email_exists' } };
    let rest: string[] = [];

    act(() => {
      rest = applyProblemToForm(form, conflict, i18n.t, { codeFields: { user_email_exists: 'externalOrderId' } });
    });

    expect(rest).toEqual([]);
    expect(form.getFieldError('externalOrderId')).toEqual(['Đã có người dùng với địa chỉ e-mail này.']);
  });

  it('returns other client errors for the form-level alert and nothing for unexpected errors', () => {
    const form = renderOrderForm();

    const lastAdmin: ApiError = { status: 409, correlationId: 'c', problem: { errorCode: 'last_admin' } };
    expect(applyProblemToForm(form, lastAdmin, i18n.t)).toEqual([
      'Đây là quản trị viên đang hoạt động cuối cùng. Hệ thống phải còn ít nhất một người.',
    ]);

    // Reported globally with the correlation id instead.
    expect(applyProblemToForm(form, { status: 500, correlationId: 'c' }, i18n.t)).toEqual([]);
    expect(applyProblemToForm(form, { status: 'FETCH_ERROR', correlationId: 'c' }, i18n.t)).toEqual([]);
    expect(applyProblemToForm(form, new Error('not an API error'), i18n.t)).toEqual([]);
  });
});

describe('translateApiError', () => {
  it('prefers the error code, then the status, then a generic message', async () => {
    const t = i18n.t;

    expect(
      translateApiError(t, { status: 409, correlationId: 'c', problem: { errorCode: 'cannot_modify_self' } }),
    ).toBe('Bạn không thể tự đổi vai trò hoặc tự khoá tài khoản của mình.');
    expect(translateApiError(t, { status: 409, correlationId: 'c', problem: { errorCode: 'some_new_code' } })).toBe(
      'Dữ liệu đã bị người khác thay đổi. Hãy tải lại rồi thử lại.',
    );
    expect(translateApiError(t, { status: 403, correlationId: 'c' })).toBe(
      'Bạn không có quyền thực hiện thao tác này.',
    );
    expect(translateApiError(t, { status: 'FETCH_ERROR', correlationId: 'c' })).toBe(
      'Không kết nối được máy chủ. Hãy kiểm tra mạng và thử lại.',
    );
    expect(translateApiError(t, { status: 503, correlationId: 'c' })).toBe('Máy chủ báo lỗi. Vui lòng thử lại sau.');
    expect(translateApiError(t, { status: 418, correlationId: 'c' })).toBe('Không thể hoàn tất yêu cầu.');
    expect(translateApiError(t, 'not an error object')).toBe('Không thể hoàn tất yêu cầu.');

    await i18n.changeLanguage('en');
    expect(translateApiError(i18n.t, { status: 409, correlationId: 'c', problem: { errorCode: 'last_admin' } })).toBe(
      'This is the last active administrator. The system must keep at least one.',
    );
  });
});
