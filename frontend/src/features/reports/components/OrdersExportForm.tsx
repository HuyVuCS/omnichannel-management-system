import { FileExcelOutlined } from '@ant-design/icons';
import { App as AntApp, Button, DatePicker, Form, Input, Select, Space } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { CustomerSelect } from '@/features/customers/components/CustomerSelect';
import { ORDER_STATUSES, type OrderStatus } from '@/features/orders/ordersApi';
import { FormErrors } from '@/shared/components/FormErrors';
import {
  DATE_RANGE_WIDTH,
  FILTER_WIDTH,
  PAGE_ACTION_WIDTH,
  PRODUCT_SELECT_WIDTH,
  SEARCH_WIDTH,
} from '@/shared/components/layout';
import { oneOf } from '@/shared/utils/address';
import { endOfLocalDay, parseLocalDate, startOfLocalDay } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT, DATE_FORMAT } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import {
  applyExportProblem,
  browserTimeZone,
  DEFAULT_RANGE_DAYS,
  defaultRange,
  MAX_EXPORT_RANGE_DAYS,
  rangeDays,
} from '../exportRequest';
import type { ExportPrefill } from '../paths';
import { useRequestOrdersExportMutation } from '../reportsApi';
import { ExportFileNote } from './ExportFileNote';

type OrdersPrefill = Extract<ExportPrefill, { reportType: 'Orders' }>;

interface OrdersExportValues {
  channel?: string;
  status?: OrderStatus;
  customerId?: string;
  search?: string;
  range?: [Dayjs | null, Dayjs | null] | null;
}

/** The API names the two ends of the range; the form has one field for both. */
const FIELD_OF = { from: 'range', to: 'range' };

/** Fields without a rule of their own: the form does not check them again, so an API error on them is removed by hand. */
const FIELDS_WITHOUT_RULE = ['channel', 'status', 'customerId', 'search'] as const;

/**
 * What the form starts with: the filters the orders list handed over (each checked again - location state is not
 * typed), and its range of days; where the list has no range, or only one end of it, the default range fills in.
 */
function initialValues(prefill: OrdersPrefill | undefined): OrdersExportValues {
  const [defaultFrom, defaultTo] = defaultRange();
  const from = parseLocalDate(prefill?.from);
  const to = parseLocalDate(prefill?.to);
  const last = to ? dayjs(to) : defaultTo;
  const first = from ? dayjs(from) : to ? last.subtract(DEFAULT_RANGE_DAYS - 1, 'day') : defaultFrom;

  return {
    channel: typeof prefill?.channel === 'string' ? prefill.channel.trim() || undefined : undefined,
    status: oneOf(ORDER_STATUSES, prefill?.status ?? null),
    customerId: isGuid(prefill?.customerId) ? prefill.customerId : undefined,
    search: typeof prefill?.search === 'string' ? prefill.search : undefined,
    range: [first, last],
  };
}

/**
 * Request of an orders export: the filters of the orders list with the same meaning, and a required range of local
 * days (sent as the instants of their start and end, like every date filter). The file's times are written in the
 * browser's time zone and its texts in the UI language - both shown, neither chosen here.
 *
 * Nothing is decided here: who may export, how long a range may be, how many rows a file may have and how many exports
 * a user may have waiting is the API's. The range rule of this form (`MAX_EXPORT_RANGE_DAYS`) only answers earlier.
 * An accepted request (202) starts nothing on this page: the export appears in the table below and is followed there.
 * The form keeps its values, so a similar export is one change away.
 */
export function OrdersExportForm({ prefill }: { prefill?: OrdersPrefill }) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<OrdersExportValues>();
  const channels = useListChannelsQuery();
  const [requestExport, request] = useRequestOrdersExportMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);
  // Once, at the first render: a later render must not move the range to another "today".
  const [initial] = useState(() => initialValues(prefill));
  const timeZone = browserTimeZone();

  const submit = async (values: OrdersExportValues) => {
    const [first, last] = values.range ?? [];
    if (!first || !last) {
      return;
    }

    setFormErrors([]);
    form.setFields(FIELDS_WITHOUT_RULE.map((name) => ({ name, errors: [] })));
    try {
      await requestExport({
        channel: values.channel || undefined,
        status: values.status || undefined,
        customerId: values.customerId || undefined,
        search: values.search?.trim() || undefined,
        from: startOfLocalDay(first.format(API_DATE_FORMAT)),
        to: endOfLocalDay(last.format(API_DATE_FORMAT)),
        timeZone,
      }).unwrap();
      message.success(t('reports.request.accepted'));
    } catch (error) {
      setFormErrors(applyExportProblem(form, error, t, FIELD_OF));
    }
  };

  return (
    <>
      <FormErrors errors={formErrors} />
      <Form
        form={form}
        // A name of its own: the ids of its fields must differ from those of the other form on the page.
        name="orders-export"
        layout="vertical"
        initialValues={initial}
        onFinish={(values) => void submit(values)}
      >
        <Space align="start" size={16} wrap>
          <Form.Item
            name="range"
            label={t('reports.form.range')}
            rules={[
              { required: true },
              {
                validator: (_rule, value: OrdersExportValues['range']) => {
                  const [first, last] = value ?? [];
                  // An empty range is the business of the rule above.
                  return first && last && rangeDays([first, last]) > MAX_EXPORT_RANGE_DAYS
                    ? Promise.reject(new Error(t('reports.form.rangeTooLong', { days: MAX_EXPORT_RANGE_DAYS })))
                    : Promise.resolve();
                },
              },
            ]}
            style={{ width: DATE_RANGE_WIDTH }}
          >
            <DatePicker.RangePicker
              format={DATE_FORMAT}
              placeholder={[t('orders.filters.from'), t('orders.filters.to')]}
              style={{ width: '100%' }}
            />
          </Form.Item>
          <Form.Item name="channel" label={t('reports.form.channel')} style={{ width: FILTER_WIDTH }}>
            <Select<string>
              allowClear
              placeholder={t('common.all')}
              loading={channels.isFetching}
              options={(channels.data ?? []).map((item) => ({ value: item.slug, label: item.name }))}
            />
          </Form.Item>
          <Form.Item name="status" label={t('reports.form.status')} style={{ width: FILTER_WIDTH }}>
            <Select<OrderStatus>
              allowClear
              placeholder={t('common.all')}
              options={ORDER_STATUSES.map((value) => ({ value, label: t(`orders.statuses.${value}`) }))}
            />
          </Form.Item>
          <Form.Item name="customerId" label={t('reports.form.customer')} style={{ width: PRODUCT_SELECT_WIDTH }}>
            <CustomerSelect allowClear placeholder={t('common.all')} />
          </Form.Item>
          <Form.Item name="search" label={t('reports.form.orderSearch')} style={{ width: SEARCH_WIDTH }}>
            <Input allowClear autoComplete="off" placeholder={t('orders.searchPlaceholder')} />
          </Form.Item>
        </Space>
        <ExportFileNote timeZone={timeZone} />
        <Button
          type="primary"
          htmlType="submit"
          icon={<FileExcelOutlined />}
          loading={request.isLoading}
          disabled={request.isLoading}
          style={{ width: PAGE_ACTION_WIDTH }}
        >
          {t('reports.request.submit')}
        </Button>
      </Form>
    </>
  );
}
